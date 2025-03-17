﻿using System.Security.Cryptography;
using Blockcore.NBitcoin;
using Blockcore.NBitcoin.DataEncoders;
using Microsoft.JSInterop;

namespace Angor.Client.Services
{
    public class EncryptionService : IEncryptionService
    {
        private readonly IJSRuntime _jsRuntime;

        public EncryptionService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task<string> EncryptData(string secretData, string password)
        {
            return await _jsRuntime.InvokeAsync<string>("encryptData", secretData, password);
        }

        public async Task<string> DecryptData(string encryptedData, string password)
        {
            return await _jsRuntime.InvokeAsync<string>("decryptData", encryptedData, password);
        }

        public async Task<string> EncryptNostrContentAsync(string nsec, string npub, string content)
        {
            //var secertHex = GetSharedSecretHexWithoutPrefix(nsec, npub);
            var secertHex = GetAesConversationKey(nsec, npub);
            return await _jsRuntime.InvokeAsync<string>("encryptNostr", secertHex, content);
        }

        public async Task<string> DecryptNostrContentAsync(string nsec, string npub, string encryptedContent)
        {
            //var secertHex = GetSharedSecretHexWithoutPrefix(nsec, npub);
            var secertHex = GetAesConversationKey(nsec, npub);
            return await _jsRuntime.InvokeAsync<string>("decryptNostr", secertHex, encryptedContent);
        }

        private static string GetSharedSecretHexWithoutPrefix(string nsec, string npub)
        {
            var privateKey = new Key(Encoders.Hex.DecodeData(nsec));
            var publicKey = new PubKey("02" + npub);
            
            var secert = publicKey.GetSharedPubkey(privateKey);
            return Encoders.Hex.EncodeData(secert.ToBytes()[1..]);
        }
        
        private static byte[] GetAesConversationKey(string nsec, string npub)
        {
            var privateKey = new Key(Encoders.Hex.DecodeData(nsec));
            var publicKey = new PubKey("02" + npub);
            
            var secert = publicKey.GetSharedPubkey(privateKey);

            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secert.ToBytes(), 32, "nip44-v2"u8.ToArray());
        }
    }
}
