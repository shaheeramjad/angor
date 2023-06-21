using System.Net.Http.Json;
using Angor.Client.Shared.Models;
using Angor.Client.Shared.Types;
using Angor.Client.Storage;
using Angor.Shared;
using Blockcore.Consensus.ScriptInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.NBitcoin;
using Blockcore.NBitcoin.BIP32;
using Blockcore.NBitcoin.BIP39;
using Blockcore.Networks;

namespace Angor.Client.Services;

public class WalletOperations : IWalletOperations 
{
    private readonly HttpClient _http;
    private readonly IClientStorage _storage;
    private readonly IWalletStorage _walletStorage;
    private readonly IHdOperations _hdOperations;
    private readonly ILogger<WalletOperations> _logger;
    private readonly INetworkConfiguration _networkConfiguration;

    public WalletOperations(HttpClient http, IClientStorage storage, IHdOperations hdOperations, ILogger<WalletOperations> logger, INetworkConfiguration networkConfiguration, IWalletStorage walletStorage)
    {
        _http = http;
        _storage = storage;
        _hdOperations = hdOperations;
        _logger = logger;
        _networkConfiguration = networkConfiguration;
        _walletStorage = walletStorage;
    }

    public string GenerateWalletWords()
    {
        var count = (WordCount)12;
        var mnemonic = new Mnemonic(Wordlist.English, count);
        string walletWords = mnemonic.ToString();
        return walletWords;
    }

    public void DeleteWallet()
    {
        Network network = _networkConfiguration.GetNetwork();
        _storage.DeleteAccountInfo(network.Name);
        _walletStorage.DeleteWallet();
    }

    public async Task<OperationResult<Transaction>> SendAmountToAddress(SendInfo sendInfo)
    {
        Network network = _networkConfiguration.GetNetwork();
        AccountInfo accountInfo = _storage.GetAccountInfo(network.Name);

        var (coins, keys) = GetUnspentOutputsForTransaction(sendInfo, accountInfo);
        if (coins == null)
        {
            return new OperationResult<Transaction> { Success = false, Message = "not enough funds" };
        }

        var builder = new TransactionBuilder(network)
            .Send(BitcoinWitPubKeyAddress.Create(sendInfo.SendToAddress, network), Money.Coins(sendInfo.SendAmount))
            .AddCoins(coins)
            .AddKeys(keys.ToArray())
            .SetChange(BitcoinWitPubKeyAddress.Create(sendInfo.ChangeAddress, network))
            .SendEstimatedFees(new FeeRate(Money.Coins(sendInfo.FeeRate)));

        var signedTransaction = builder.BuildTransaction(true);

        var hex = signedTransaction.ToHex(network.Consensus.ConsensusFactory);

        var indexer = _networkConfiguration.getIndexerUrl();
        
        var endpoint = Path.Combine(indexer.Url, "command/send");

        var res = await _http.PostAsync(endpoint, new StringContent(hex));

        if (res.IsSuccessStatusCode)
            return new OperationResult<Transaction> { Success = true, Data = signedTransaction };

        var content = await res.Content.ReadAsStringAsync();

        return new OperationResult<Transaction> { Success = false, Message = res.ReasonPhrase + content };
    }

    private void FindOutputsForTransaction(SendInfo sendInfo, AccountInfo accountInfo)
    {
        var utxos = accountInfo.AddressesInfo.Concat(accountInfo.ChangeAddressesInfo);

        var utxosToSpend = new List<UtxoDataWithPath>();

        long total = 0;
        foreach (var utxoData in utxos.SelectMany(_ => _.UtxoData
                         .Select(u => new { path = _.HdPath, utxo = u }))
                     .OrderBy(o => o.utxo.blockIndex)
                     .ThenByDescending(o => o.utxo.value))
        {
            utxosToSpend.Add(new UtxoDataWithPath { HdPath = utxoData.path, UtxoData = utxoData.utxo });

            total += utxoData.utxo.value;

            if (total > sendInfo.SendAmountSat)
            {
                break;
            }
        }

        if (total < sendInfo.SendAmountSat)
        {
            throw new ApplicationException("Not enough funds");
        }

        foreach (var data in utxosToSpend)
        {
            sendInfo.SendUtxos.Add(data.UtxoData.outpoint.ToString(), data);
        }
    }

    private (List<Coin>? coins,List<Key> keys) GetUnspentOutputsForTransaction(SendInfo sendInfo, AccountInfo accountInfo)
    {
        if (sendInfo.SendAmountSat > sendInfo.SendUtxos.Sum(s => s.Value.UtxoData.value))
        {
            throw new ApplicationException("not enough funds");
        }

        ExtKey extendedKey;
        try
        {
            var data = _walletStorage.GetWallet();
            extendedKey = _hdOperations.GetExtendedKey(data.Words, data.Passphrase);
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine("Exception occurred: {0}", ex.ToString());

            if (ex.Message == "Unknown")
                throw new Exception("Please make sure you enter valid mnemonic words.");

            throw;
        }

        var coins = new List<Coin>();
        var keys = new List<Key>();

        foreach (var item in sendInfo.SendUtxos)
        {
            var utxo = item.Value.UtxoData;

            coins.Add(new Coin(uint256.Parse(utxo.outpoint.transactionId), (uint)utxo.outpoint.outputIndex,
                Money.Satoshis(utxo.value), Script.FromHex(utxo.scriptHex)));

            // derive the private key
            var extKey = extendedKey.Derive(new KeyPath(item.Value.HdPath));
            Key privateKey = extKey.PrivateKey;
            keys.Add(privateKey);
        }

        return (coins,keys);
    }

    public void BuildAccountInfoForWalletWords()
    {
        ExtKey.UseBCForHMACSHA512 = true;
        Blockcore.NBitcoin.Crypto.Hashes.UseBCForHMACSHA512 = true;

        Network network = _networkConfiguration.GetNetwork();
        var coinType = network.Consensus.CoinType;
        var accountIndex = 0; // for now only account 0
        var purpose = 84; // for now only legacy

        AccountInfo accountInfo = _storage.GetAccountInfo(network.Name);

        if (accountInfo != null)
            return;

        accountInfo = new AccountInfo();

        ExtKey extendedKey;
        try
        {
            var data = _walletStorage.GetWallet();
            extendedKey = _hdOperations.GetExtendedKey(data.Words, data.Passphrase);
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine("Exception occurred: {0}", ex.ToString());

            if (ex.Message == "Unknown")
                throw new Exception("Please make sure you enter valid mnemonic words.");

            throw;
        }

        string accountHdPath = _hdOperations.GetAccountHdPath(purpose, coinType, accountIndex);
        Key privateKey = extendedKey.PrivateKey;
        _storage.SetWalletPubkey(privateKey.PubKey.ToHex());

        ExtPubKey accountExtPubKeyTostore =
            _hdOperations.GetExtendedPublicKey(privateKey, extendedKey.ChainCode, accountHdPath);

        accountInfo.ExtPubKey = accountExtPubKeyTostore.ToString(network);
        accountInfo.Path = accountHdPath;

        _storage.SetAccountInfo(network.Name, accountInfo);
    }

    public async Task<AccountInfo> FetchDataForExistingAddressesAsync()
    {
        ExtKey.UseBCForHMACSHA512 = true;
        Blockcore.NBitcoin.Crypto.Hashes.UseBCForHMACSHA512 = true;

        Network network = _networkConfiguration.GetNetwork();

        AccountInfo accountInfo = _storage.GetAccountInfo(network.Name);
        
        foreach (var addressInfo in accountInfo.AddressesInfo)
        {
            if (!addressInfo.UtxoData.Any()) continue;
            
            var result = await FetchUtxoForAddressAsync(addressInfo.Address);

            if (result.data.Count == addressInfo.UtxoData.Count)
            {
                for (var i = 0; i < result.data.Count - 1; i++)
                {
                    if (result.data[i].outpoint.transactionId == addressInfo.UtxoData[i].outpoint.transactionId) 
                        continue;
                    addressInfo.UtxoData.Clear();
                    addressInfo.UtxoData.AddRange(result.data);
                    break;
                }
            }
            else
            {
                addressInfo.UtxoData.Clear();
                addressInfo.UtxoData.AddRange(result.data);
            }
        }

        foreach (var changeAddressInfo in accountInfo.ChangeAddressesInfo)
        {
            if (!changeAddressInfo.HasHistory) continue;
            
            var result = await FetchUtxoForAddressAsync(changeAddressInfo.Address);

            if (result.data.Count == changeAddressInfo.UtxoData.Count)
            {
                for (var i = 0; i < result.data.Count - 1; i++)
                {
                    if (result.data[i].outpoint.transactionId == changeAddressInfo.UtxoData[i].outpoint.transactionId) 
                        continue;
                    changeAddressInfo.UtxoData.Clear();
                    changeAddressInfo.UtxoData.AddRange(result.data);
                    break;
                }
            }
            else
            {
                changeAddressInfo.UtxoData.Clear();
                changeAddressInfo.UtxoData.AddRange(result.data);
            }
        }

        _storage.SetAccountInfo(network.Name, accountInfo);
        
        return accountInfo;
    }

    public async Task<AccountInfo> FetchDataForNewAddressesAsync()
    {
        ExtKey.UseBCForHMACSHA512 = true;
        Blockcore.NBitcoin.Crypto.Hashes.UseBCForHMACSHA512 = true;

        Network network = _networkConfiguration.GetNetwork();

        AccountInfo accountInfo = _storage.GetAccountInfo(network.Name);

        var (index, items) = await FetcAddressesDataForPubKeyAsync(accountInfo.LastFetchIndex, accountInfo.ExtPubKey, network, false);

        accountInfo.LastFetchIndex = index;
        foreach (var addressInfo in items)
        {
            var addressInfoToDelete = accountInfo.AddressesInfo.SingleOrDefault(_ => _.Address == addressInfo.Address);
            if (addressInfoToDelete != null)
                accountInfo.AddressesInfo.Remove(addressInfoToDelete);
            
            accountInfo.AddressesInfo.Add(addressInfo);
            accountInfo.TotalBalance += addressInfo.Balance;
        }

        var (changeIndex, changeItems) = await FetcAddressesDataForPubKeyAsync(accountInfo.LastFetchChangeIndex, accountInfo.ExtPubKey, network, true);

        accountInfo.LastFetchChangeIndex = changeIndex;
        foreach (var changeAddressInfo in changeItems)
        {
            var addressInfoToDelete = accountInfo.ChangeAddressesInfo.SingleOrDefault(_ => _.Address == changeAddressInfo.Address);
            if (addressInfoToDelete != null) 
                accountInfo.ChangeAddressesInfo.Remove(addressInfoToDelete);
            
            accountInfo.ChangeAddressesInfo.Add(changeAddressInfo);
            accountInfo.TotalBalance += changeAddressInfo.Balance;
        }

        _storage.SetAccountInfo(network.Name, accountInfo);

        return accountInfo;
    }

    private async Task<(int,List<AddressInfo>)> FetcAddressesDataForPubKeyAsync(int scanIndex, string ExtendedPubKey, Network network, bool isChange)
    {
        ExtPubKey accountExtPubKey = ExtPubKey.Parse(ExtendedPubKey, network);
        
        var addressesInfo = new List<AddressInfo>();
        var accountIndex = 0; // for now only account 0
        var purpose = 84; // for now only legacy
        
        var gap = 5;
        while (gap > 0)
        {
            PubKey pubkey = _hdOperations.GeneratePublicKey(accountExtPubKey, scanIndex, isChange);
            var path = _hdOperations.CreateHdPath(purpose, network.Consensus.CoinType, accountIndex, isChange, scanIndex);
            
            var address = pubkey.GetSegwitAddress(network).ToString();
            var result = await FetchUtxoForAddressAsync(address);

            addressesInfo.Add(new AddressInfo
                { Address = address, HdPath = path, UtxoData = result.data, HasHistory = !result.noHistory });
            scanIndex++;

            if (!result.noHistory) continue;
            
            gap--;
        }

        return (scanIndex, addressesInfo);
    }

    public async Task<(bool noHistory, List<UtxoData> data)> FetchUtxoForAddressAsync(string address)
    {
        var limit = 50;
        var offset = 0;
        List<UtxoData> allItems = new();

        var urlBalance = $"/query/address/{address}";
        IndexerUrl indexer = _networkConfiguration.getIndexerUrl();
        var addressBalance = await _http.GetFromJsonAsync<AddressBalance>(indexer.Url + urlBalance);

        if (addressBalance?.balance == 0 && (addressBalance.totalReceivedCount + addressBalance.totalSentCount) == 0)
        {
            return (true, allItems);
        }

        int fetchCount = 50; // for the demo we just scan 50 addresses

        for (int i = 0; i < fetchCount; i++)
        {
            // this is inefficient look at headers to know when to stop

            var url = $"/query/address/{address}/transactions/unspent?confirmations=0&offset={offset}&limit={limit}";

            Console.WriteLine($"fetching {url}");

            var response = await _http.GetAsync(indexer.Url + url);
            var utxo = await response.Content.ReadFromJsonAsync<List<UtxoData>>();

            if (utxo == null || !utxo.Any())
                break;

            allItems.AddRange(utxo);

            offset += limit;
        }

        return (false, allItems);
    }

    public async Task<IEnumerable<FeeEstimation>> GetFeeEstimationAsync()
    {
        var blocks = new []{1,5,10};

        try
        {
            IndexerUrl indexer = _networkConfiguration.getIndexerUrl();

            var url = blocks.Aggregate("/stats/fee?", (current, block) => current + $"confirmations={block}");

            _logger.LogInformation($"fetching fee estimation for blocks - {url}");

            var response = await _http.GetAsync(indexer.Url + url);
            
            var feeEstimations = await response.Content.ReadFromJsonAsync<FeeEstimations>();

            if (feeEstimations == null || (!feeEstimations.Fees?.Any() ?? true))
                return blocks.Select(_ => new FeeEstimation{Confirmations = _,FeeRate = 10000 / _});

            return feeEstimations.Fees;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    public void CalculateTransactionFee(SendInfo sendInfo, long feeRate)
    {
        var network = _networkConfiguration.GetNetwork();

        var accountInfo = _storage.GetAccountInfo(network.Name);
        
        if (sendInfo.SendUtxos.Count == 0)
        {
            FindOutputsForTransaction(sendInfo, accountInfo);

            if (sendInfo.SendUtxos.Count == 0) // something went wrong
                throw new ArgumentNullException();
        }

        if (string.IsNullOrEmpty(sendInfo.ChangeAddress))
        {
            sendInfo.ChangeAddress = accountInfo.ChangeAddressesInfo.First(f => f.HasHistory == false).Address;
        }

        var coins = sendInfo.SendUtxos
            .Select(_ => _.Value.UtxoData)
            .Select(_ => new Coin(uint256.Parse(_.outpoint.transactionId), (uint)_.outpoint.outputIndex,
                Money.Satoshis(_.value), Script.FromHex(_.scriptHex)));

        var builder = new TransactionBuilder(network)
            .Send(BitcoinWitPubKeyAddress.Create(sendInfo.SendToAddress, network), sendInfo.SendAmountSat)
            .AddCoins(coins)
            .SetChange(BitcoinWitPubKeyAddress.Create(sendInfo.ChangeAddress, network));

        sendInfo.SendFee = builder.EstimateFees(new FeeRate(Money.Satoshis(feeRate))).ToUnit(MoneyUnit.BTC);
    }
}