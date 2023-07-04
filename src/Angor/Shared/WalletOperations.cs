using System.Net.Http.Json;
using Angor.Shared.Models;
using Blockcore.Consensus.ScriptInfo;
using Blockcore.Consensus.TransactionInfo;
using Blockcore.NBitcoin;
using Blockcore.NBitcoin.BIP32;
using Blockcore.NBitcoin.BIP39;
using Blockcore.Networks;
using Microsoft.Extensions.Logging;

namespace Angor.Shared;

public class WalletOperations : IWalletOperations 
{
    private readonly HttpClient _http;
    private readonly IHdOperations _hdOperations;
    private readonly ILogger<WalletOperations> _logger;
    private readonly INetworkConfiguration _networkConfiguration;

    private const int AccountIndex = 0; // for now only account 0
    private const int Purpose = 84; // for now only legacy

    public WalletOperations(HttpClient http, IHdOperations hdOperations, ILogger<WalletOperations> logger, INetworkConfiguration networkConfiguration)
    {
        _http = http;
        _hdOperations = hdOperations;
        _logger = logger;
        _networkConfiguration = networkConfiguration;
    }

    public string GenerateWalletWords()
    {
        var count = (WordCount)12;
        var mnemonic = new Mnemonic(Wordlist.English, count);
        string walletWords = mnemonic.ToString();
        return walletWords;
    }

    public async Task<OperationResult<Transaction>> SendAmountToAddress(WalletWords walletWords, SendInfo sendInfo) //TODO change the passing of wallet words as parameter after refactoring is complete
    {
        Network network = _networkConfiguration.GetNetwork();

        if (sendInfo.SendAmountSat > sendInfo.SendUtxos.Values.Sum(s => s.UtxoData.value))
        {
            throw new ApplicationException("not enough funds");
        }
        
        var (coins, keys) =
            GetUnspentOutputsForTransaction(walletWords,sendInfo.SendUtxos.Values.ToList());
        
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

        var indexer = _networkConfiguration.GetIndexerUrl();
        
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

    public (List<Coin>? coins,List<Key> keys) GetUnspentOutputsForTransaction(WalletWords walletWords , List<UtxoDataWithPath> utxoDataWithPaths)
    {
        ExtKey extendedKey;
        try
        {
            extendedKey = _hdOperations.GetExtendedKey(walletWords.Words, walletWords.Passphrase); //TODO change this to be the extended key 
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine("Exception occurred: {0}", ex);

            if (ex.Message == "Unknown")
                throw new Exception("Please make sure you enter valid mnemonic words.");

            throw;
        }

        var coins = new List<Coin>();
        var keys = new List<Key>();

        foreach (var utxoDataWithPath in utxoDataWithPaths)
        {
            var utxo = utxoDataWithPath.UtxoData;

            coins.Add(new Coin(uint256.Parse(utxo.outpoint.transactionId), (uint)utxo.outpoint.outputIndex,
                Money.Satoshis(utxo.value), Script.FromHex(utxo.scriptHex)));

            // derive the private key
            var extKey = extendedKey.Derive(new KeyPath(utxoDataWithPath.HdPath));
            Key privateKey = extKey.PrivateKey;
            keys.Add(privateKey);
        }

        return (coins,keys);
    }

    public AccountInfo BuildAccountInfoForWalletWords(WalletWords walletWords)
    {
        ExtKey.UseBCForHMACSHA512 = true;
        Blockcore.NBitcoin.Crypto.Hashes.UseBCForHMACSHA512 = true;

        Network network = _networkConfiguration.GetNetwork();
        var coinType = network.Consensus.CoinType;

        //AccountInfo accountInfo = _storage.GetAccountInfo(network.Name);
        //
        // if (accountInfo != null)
        //     return accountInfo;

        var accountInfo = new AccountInfo();

        ExtKey extendedKey;
        try
        {
           // var data = _walletStorage.GetWallet();
            extendedKey = _hdOperations.GetExtendedKey(walletWords.Words, walletWords.Passphrase);
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine("Exception occurred: {0}", ex.ToString());

            if (ex.Message == "Unknown")
                throw new Exception("Please make sure you enter valid mnemonic words.");

            throw;
        }

        string accountHdPath = _hdOperations.GetAccountHdPath(Purpose, coinType, AccountIndex);
        Key privateKey = extendedKey.PrivateKey;
        // _storage.SetWalletPubkey(privateKey.PubKey.ToHex());

        ExtPubKey accountExtPubKeyTostore =
            _hdOperations.GetExtendedPublicKey(privateKey, extendedKey.ChainCode, accountHdPath);

        accountInfo.ExtPubKey = accountExtPubKeyTostore.ToString(network);
        accountInfo.Path = accountHdPath;

        //_storage.SetAccountInfo(network.Name, accountInfo);
        return accountInfo;
    }

    public async Task<AccountInfo> FetchDataForExistingAddressesAsync(AccountInfo accountInfo)
    {
        ExtKey.UseBCForHMACSHA512 = true;
        Blockcore.NBitcoin.Crypto.Hashes.UseBCForHMACSHA512 = true;

        //Network network = _networkConfiguration.GetNetwork();

        //AccountInfo accountInfo = _storage.GetAccountInfo(network.Name);

        var addressTasks=  accountInfo.AddressesInfo.Select(UpdateAddressInfoUtxoData);
        
        var changeAddressTasks=  accountInfo.ChangeAddressesInfo.Select(UpdateAddressInfoUtxoData);

        await Task.WhenAll(addressTasks.Concat(changeAddressTasks));

       // _storage.SetAccountInfo(network.Name, accountInfo);
        
        return accountInfo;
    }

    private async Task UpdateAddressInfoUtxoData(AddressInfo addressInfo)
    {
        if (!addressInfo.UtxoData.Any() && addressInfo.HasHistory) return;

        var (_, utxoList) = await FetchUtxoForAddressAsync(addressInfo.Address);
        
        if (utxoList.Count != addressInfo.UtxoData.Count ||
            utxoList.Where((_, i) => _.outpoint.transactionId != addressInfo.UtxoData[i].outpoint.transactionId)
                .Any())
        {
            addressInfo.UtxoData.Clear();
            addressInfo.UtxoData.AddRange(utxoList);
        }
    }

    public async Task<AccountInfo> FetchDataForNewAddressesAsync(AccountInfo accountInfo)
    {
        ExtKey.UseBCForHMACSHA512 = true;
        Blockcore.NBitcoin.Crypto.Hashes.UseBCForHMACSHA512 = true;

        Network network = _networkConfiguration.GetNetwork();

        //AccountInfo accountInfo = _storage.GetAccountInfo(network.Name);

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

        //_storage.SetAccountInfo(network.Name, accountInfo);

        return accountInfo;
    }

    private async Task<(int,List<AddressInfo>)> FetcAddressesDataForPubKeyAsync(int scanIndex, string ExtendedPubKey, Network network, bool isChange)
    {
        ExtPubKey accountExtPubKey = ExtPubKey.Parse(ExtendedPubKey, network);
        
        var addressesInfo = new List<AddressInfo>();

        var gap = 5;
        AddressInfo? newEmptyAddress = null;
        AddressBalance[] addressesNotEmpty;
        do
        {
            var newAddressesToCheck = Enumerable.Range(0, gap)
                .Select(_ => GenerateAddressFromPubKey(scanIndex + _, network, isChange, accountExtPubKey))
                .ToList();

            //check all new addresses for balance or a history
            var urlBalance = "/query/addresses/balance";
            var indexer = _networkConfiguration.GetIndexerUrl();
            var response = await _http.PostAsJsonAsync(indexer.Url + urlBalance,
                newAddressesToCheck.Select(_ => _.Address).ToArray());

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.ReasonPhrase);

            addressesNotEmpty = (await response.Content.ReadFromJsonAsync<AddressBalance[]>())?.ToArray() ?? Array.Empty<AddressBalance>();

            if (addressesNotEmpty.Length < newAddressesToCheck.Count)
                newEmptyAddress = newAddressesToCheck[addressesNotEmpty.Length];

            if (!addressesNotEmpty.Any())
                break; //No new data for the addresses checked
            
            //Add the addresses with balance or a history to the returned list
            addressesInfo.AddRange(newAddressesToCheck
                .Where(addressInfo => addressesNotEmpty
                    .Any(_ => _.address == addressInfo.Address)));

            var tasks = addressesNotEmpty.Select(_ => FetchUtxoForAddressAsync(_.address));

            var lookupResults = await Task.WhenAll(tasks);

            foreach (var (address, data) in lookupResults)
            {
                var addressInfo = addressesInfo.First(_ => _.Address == address);
                addressInfo.HasHistory = true;
                addressInfo.UtxoData = data;
            }

            scanIndex += addressesNotEmpty.Length;

        } while (addressesNotEmpty.Any());

        if (newEmptyAddress != null) //empty address for receiving funds
            addressesInfo.Add(newEmptyAddress);
        
        return (scanIndex, addressesInfo);
    }

    private AddressInfo GenerateAddressFromPubKey(int scanIndex, Network network, bool isChange, ExtPubKey accountExtPubKey)
    {
        var pubKey = _hdOperations.GeneratePublicKey(accountExtPubKey, scanIndex, isChange);
        var path = _hdOperations.CreateHdPath(Purpose, network.Consensus.CoinType, AccountIndex, isChange, scanIndex);
        var address = pubKey.GetSegwitAddress(network).ToString();

        return new AddressInfo { Address = address, HdPath = path };
    }

    public async Task<(string address, List<UtxoData> data)> FetchUtxoForAddressAsync(string address)
    {
        var limit = 50;
        var offset = 0;
        List<UtxoData> allItems = new();
        
        IndexerUrl indexer = _networkConfiguration.GetIndexerUrl();

        do
        {
            // this is inefficient look at headers to know when to stop

            var url = $"/query/address/{address}/transactions/unspent?confirmations=0&offset={offset}&limit={limit}";

            Console.WriteLine($"fetching {url}");

            var response = await _http.GetAsync(indexer.Url + url);
            var utxo = await response.Content.ReadFromJsonAsync<List<UtxoData>>();

            if (utxo == null || !utxo.Any())
                break;

            allItems.AddRange(utxo);

            if (utxo.Count < limit)
                break;

            offset += limit;
        } while (true);

        return (address, allItems);
    }

    public async Task<IEnumerable<FeeEstimation>> GetFeeEstimationAsync()
    {
        var blocks = new []{1,5,10};

        try
        {
            IndexerUrl indexer = _networkConfiguration.GetIndexerUrl();

            var url = blocks.Aggregate("/stats/fee?", (current, block) => current + $@"confirmations={block}&");

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

    public decimal CalculateTransactionFee(SendInfo sendInfo,AccountInfo accountInfo, long feeRate)
    {
        var network = _networkConfiguration.GetNetwork();

        //var accountInfo = _storage.GetAccountInfo(network.Name);
        
        if (sendInfo.SendUtxos.Count == 0)
        {
            FindOutputsForTransaction(sendInfo, accountInfo);

            if (sendInfo.SendUtxos.Count == 0) // something went wrong
                throw new ArgumentNullException();
        }

        // if (string.IsNullOrEmpty(sendInfo.ChangeAddress)) TODO move to the right location in the caller 
        // {
        //     sendInfo.ChangeAddress = accountInfo.ChangeAddressesInfo.First(f => f.HasHistory == false).Address;
        // }

        var coins = sendInfo.SendUtxos
            .Select(_ => _.Value.UtxoData)
            .Select(_ => new Coin(uint256.Parse(_.outpoint.transactionId), (uint)_.outpoint.outputIndex,
                Money.Satoshis(_.value), Script.FromHex(_.scriptHex)));

        var builder = new TransactionBuilder(network)
            .Send(BitcoinWitPubKeyAddress.Create(sendInfo.SendToAddress, network), sendInfo.SendAmountSat)
            .AddCoins(coins)
            .SetChange(BitcoinWitPubKeyAddress.Create(sendInfo.ChangeAddress, network));

        return builder.EstimateFees(new FeeRate(Money.Satoshis(feeRate))).ToUnit(MoneyUnit.BTC);
    }
}