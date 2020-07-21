using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System;
using System.ComponentModel;
using System.Numerics;

[assembly: Features(ContractPropertyState.HasStorage | ContractPropertyState.HasDynamicInvoke | ContractPropertyState.Payable)]

namespace Nep5Proxy
{
    public class Nep5ProxyPip1 : SmartContract
    {
        // Constants
        private static readonly byte[] CCMCScriptHash = "".HexToBytes(); // little endian
        private static readonly byte[] Operator = "".ToScriptHash(); // Operator address

        // Dynamic Call
        private delegate object DynCall(string method, object[] args); // dynamic call

        // Events
        public static event Action<byte[], BigInteger, byte[], byte[]> DelegateAssetEvent;
        public static event Action<byte[], byte[], BigInteger, byte[], byte[], BigInteger> LockEvent;
        public static event Action<byte[], byte[], BigInteger> UnlockEvent;
        public static event Action<byte[], BigInteger, byte[], BigInteger> BindAssetHashEvent;
        public static event Action<BigInteger, byte[]> BindProxyHashEvent;

        // Storage prefix
        private static readonly byte[] FromAssetListPrefix = new byte[] { 0x01, 0x01 }; // "FromAssetList";

        public static object Main(string method, object[] args)
        {
            if (Runtime.Trigger == TriggerType.Application)
            {
                var callscript = ExecutionEngine.CallingScriptHash;

                if (method == "getAssetBalance")
                    return GetAssetBalance((byte[])args[0]);
                if (method == "delegateAsset")
                    return DelegateAsset((BigInteger)args[0], (byte[])args[1], (byte[])args[2], (BigInteger)args[3], callscript);
                if (method == "registerAsset")
                    return RegisterAsset((byte[])args[0], (byte[])args[1], (BigInteger)args[2], callscript);
                if (method == "lock")
                    return Lock((byte[])args[0], (byte[])args[1], (BigInteger)args[2], (byte[])args[3], (BigInteger)args[4]);
                if (method == "unlock")
                    return Unlock((byte[])args[0], (byte[])args[1], (BigInteger)args[2], callscript);
            }
            return false;
        }

        [DisplayName("getAssetBalance")]
        public static BigInteger GetAssetBalance(byte[] assetHash)
        {
            byte[] currentHash = ExecutionEngine.ExecutingScriptHash; // this proxy contract hash
            var nep5Contract = (DynCall)assetHash.ToDelegate();
            BigInteger balance = (BigInteger)nep5Contract("balanceOf", new object[] { currentHash });
            return balance;
        }

        // used to delegate an asset to be managed by this contract
        [DisplayName("delegateAsset")]
        public static bool DelegateAsset(BigInteger nativeChainId, byte[] nativeLockProxy, byte[] nativeAssetHash, BigInteger delegatedSupply, byte[] assetHash)
        {
          if (nativeChainId == 0)
          {
            Runtime.Notify("The parameter nativeChainId must not be zero");
            return false;
          }
          if (nativeLockProxy.Length == 0)
          {
            Runtime.Notify("The parameter nativeLockProxy must not be empty");
            return false;
          }
          if (nativeAssetHash.Length == 0)
          {
            Runtime.Notify("The parameter nativeAssetHash must not be empty");
            return false;
          }

          byte[] key = GetRegistryKey(assetHash, nativeChainId, nativeLockProxy, nativeAssetHash);

          StorageMap registry = Storage.CurrentContext.CreateMap(nameof(registry));
          if (registry.Get(key).length != 0)
          {
            Runtime.Notify("This asset has already been registered");
            return false;
          }

          StorageMap balances = Storage.CurrentContext.CreateMap(nameof(balances));
          if (balances.Get(key).length != 0)
          {
            Runtime.Notify("The balance for this asset must be zero");
            return false;
          }

          if (GetAssetBalance(assetHash) != delegatedSupply)
          {
            Runtime.Notify("The controlled balance does not match the delegatedSupply param");
            return false;
          }

          // mark asset in registry
          registry.Put(key, 0x01);

          var inputArgs = SerializeRegisterAssetArgs(assetHash, nativeAssetHash);

          // construct params for CCMC
          var param = new object[] { nativeChainId, nativeLockProxy, "registerAsset", inputArgs };
          // dynamic call CCMC
          var ccmc = (DynCall)CCMCScriptHash.ToDelegate();
          success = (bool)ccmc("CrossChain", param);
          if (!success)
          {
            Runtime.Notify("Failed to call CCMC.");
            return false;
          }

          DelegateAssetEvent(assetHash, nativeChainId, nativeLockProxy, nativeAssetHash);

          return true;
        }

        // called by the CCM to register assets from a connected chain
        [DisplayName("registerAsset")]
        public static bool RegisterAsset(byte[] inputBytes, byte[] fromProxyContract, BigInteger fromChainId, byte[] caller)
        {
          //only allowed to be called by CCMC
          if (caller.AsBigInteger() != CCMCScriptHash.AsBigInteger())
          {
            Runtime.Notify("Only allowed to be called by CCMC");
            Runtime.Notify(caller);
            Runtime.Notify(CCMCScriptHash);
            return false;
          }

          object[] results = DeserializeRegisterAssetArgs(inputBytes);
          var assetHash = (byte[])results[0];
          var nativeAssetHash = (byte[])results[1];

          byte[] key = GetRegistryKey(nativeAssetHash, fromChainId, fromProxyContract, assetHash);

          StorageMap registry = Storage.CurrentContext.CreateMap(nameof(registry));
          if (registry.Get(key).length != 0)
          {
            Runtime.Notify("This asset has already been registered");
            return false;
          }

          // mark asset in registry
          registry.Put(key, 0x01);

          return true;
        }

        // used to lock asset into proxy contract
        [DisplayName("lock")]
        public static bool Lock(byte[] fromAssetHash, byte[] fromAddress, BigInteger toChainId, byte[] toAddress, BigInteger amount)
        {
            if (fromAssetHash.Length != 20)
            {
                Runtime.Notify("The parameter fromAssetHash SHOULD be 20-byte long.");
                return false;
            }
            if (fromAddress.Length != 20)
            {
                Runtime.Notify("The parameter fromAddress SHOULD be 20-byte long.");
                return false;
            }
            if (toAddress.Length == 0)
            {
                Runtime.Notify("The parameter toAddress SHOULD not be empty.");
                return false;
            }
            if (amount < 0)
            {
                Runtime.Notify("The parameter amount SHOULD not be less than 0.");
                return false;
            }

            // get the corresbonding asset on target chain
            var toAssetHash = GetAssetHash(fromAssetHash, toChainId);
            if (toAssetHash.Length == 0)
            {
                Runtime.Notify("Target chain asset hash not found.");
                return false;
            }

            // get the proxy contract on target chain
            var toProxyHash = GetProxyHash(toChainId);
            if (toProxyHash.Length == 0)
            {
                Runtime.Notify("Target chain proxy contract not found.");
                return false;
            }

            // transfer asset from fromAddress to proxy contract address, use dynamic call to call nep5 token's contract "transfer"
            byte[] currentHash = ExecutionEngine.ExecutingScriptHash; // this proxy contract hash
            var nep5Contract = (DynCall)fromAssetHash.ToDelegate();
            bool success = (bool)nep5Contract("transfer", new object[] { fromAddress, currentHash, amount });
            if (!success)
            {
                Runtime.Notify("Failed to transfer NEP5 token to proxy contract.");
                return false;
            }

            // construct args for proxy contract on target chain
            var inputArgs = SerializeArgs(toAssetHash, toAddress, amount);
            // construct params for CCMC
            var param = new object[] { toChainId, toProxyHash, "unlock", inputArgs };
            // dynamic call CCMC
            var ccmc = (DynCall)CCMCScriptHash.ToDelegate();
            success = (bool)ccmc("CrossChain", param);
            if (!success)
            {
                Runtime.Notify("Failed to call CCMC.");
                return false;
            }

            LockEvent(fromAssetHash, fromAddress, toChainId, toAssetHash, toAddress, amount);

            return true;
        }

#if DEBUG
        [DisplayName("unlock")] //Only for ABI file
        public static bool Unlock(byte[] inputBytes, byte[] fromProxyContract, BigInteger fromChainId) => true;
#endif

        // Methods of actual execution, used to unlock asset from proxy contract
        private static bool Unlock(byte[] inputBytes, byte[] fromProxyContract, BigInteger fromChainId, byte[] caller)
        {
            //only allowed to be called by CCMC
            if (caller.AsBigInteger() != CCMCScriptHash.AsBigInteger())
            {
                Runtime.Notify("Only allowed to be called by CCMC");
                Runtime.Notify(caller);
                Runtime.Notify(CCMCScriptHash);
                return false;
            }

            byte[] storedProxy = GetProxyHash(fromChainId);

            // check the fromContract is stored, so we can trust it
            if (fromProxyContract.AsBigInteger() != storedProxy.AsBigInteger())
            {
                Runtime.Notify("From proxy contract not found.");
                Runtime.Notify(fromProxyContract);
                Runtime.Notify(fromChainId);
                Runtime.Notify(storedProxy);
                return false;
            }

            // parse the args bytes constructed in source chain proxy contract, passed by multi-chain
            object[] results = DeserializeArgs(inputBytes);
            var toAssetHash = (byte[])results[0];
            var toAddress = (byte[])results[1];
            var amount = (BigInteger)results[2];
            if (toAssetHash.Length != 20)
            {
                Runtime.Notify("ToChain Asset script hash SHOULD be 20-byte long.");
                return false;
            }
            if (toAddress.Length != 20)
            {
                Runtime.Notify("ToChain Account address SHOULD be 20-byte long.");
                return false;
            }
            if (amount < 0)
            {
                Runtime.Notify("ToChain Amount SHOULD not be less than 0.");
                return false;
            }

            // transfer asset from proxy contract to toAddress
            byte[] currentHash = ExecutionEngine.ExecutingScriptHash; // this proxy contract hash
            var nep5Contract = (DynCall)toAssetHash.ToDelegate();
            bool success = (bool)nep5Contract("transfer", new object[] { currentHash, toAddress, amount });
            if (!success)
            {
                Runtime.Notify("Failed to transfer NEP5 token to toAddress.");
                return false;
            }

            UnlockEvent(toAssetHash, toAddress, amount);

            return true;
        }

        private static byte[] GetRegistryKey(byte[] assetHash, BigInteger nativeChainId, byte[] nativeLockProxy, byte[] nativeAssetHash)
        {
          return Hash256(assetHash.Concat(nativeChainId.AsByteArray()).Concat(nativeLockProxy).Concat(nativeAssetHash))
        }

        private static object[] ReadUint255(byte[] buffer, int offset)
        {
            if (offset + 32 > buffer.Length)
            {
                Runtime.Notify("Length is not long enough");
                return new object[] { 0, -1 };
            }
            return new object[] { buffer.Range(offset, 32).ToBigInteger(), offset + 32 };
        }

        // return [BigInteger: value, int: offset]
        private static object[] ReadVarInt(byte[] buffer, int offset)
        {
            var res = ReadBytes(buffer, offset, 1); // read the first byte
            var fb = (byte[])res[0];
            if (fb.Length != 1)
            {
                Runtime.Notify("Wrong length");
                return new object[] { 0, -1 };
            }
            var newOffset = (int)res[1];
            if (fb == new byte[] { 0xFD })
            {
                return new object[] { buffer.Range(newOffset, 2).ToBigInteger(), newOffset + 2 };
            }
            else if (fb == new byte[] { 0xFE })
            {
                return new object[] { buffer.Range(newOffset, 4).ToBigInteger(), newOffset + 4 };
            }
            else if (fb == new byte[] { 0xFF })
            {
                return new object[] { buffer.Range(newOffset, 8).ToBigInteger(), newOffset + 8 };
            }
            else
            {
                return new object[] { fb.ToBigInteger(), newOffset };
            }
        }

        // return [byte[], new offset]
        private static object[] ReadVarBytes(byte[] buffer, int offset)
        {
            var res = ReadVarInt(buffer, offset);
            var count = (int)res[0];
            var newOffset = (int)res[1];
            return ReadBytes(buffer, newOffset, count);
        }

        // return [byte[], new offset]
        private static object[] ReadBytes(byte[] buffer, int offset, int count)
        {
            if (offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            return new object[] { buffer.Range(offset, count), offset + count };
        }

        private static byte[] SerializeArgs(byte[] assetHash, byte[] address, BigInteger amount)
        {
            var buffer = new byte[] { };
            buffer = WriteVarBytes(assetHash, buffer);
            buffer = WriteVarBytes(address, buffer);
            buffer = WriteUint255(amount, buffer);
            return buffer;
        }

        private static object[] DeserializeArgs(byte[] buffer)
        {
            var offset = 0;
            var res = ReadVarBytes(buffer, offset);
            var assetAddress = res[0];

            res = ReadVarBytes(buffer, (int)res[1]);
            var toAddress = res[0];

            res = ReadUint255(buffer, (int)res[1]);
            var amount = res[0];

            return new object[] { assetAddress, toAddress, amount };
        }

        private static byte[] SerializeRegisterAssetArgs(byte[] assetHash, byte[] nativeAssetHash)
        {
            var buffer = new byte[] { };
            buffer = WriteVarBytes(assetHash, buffer);
            buffer = WriteVarBytes(nativeAssetHash, buffer);
            return buffer;
        }

        private static object[] DeserializeRegisterAssetArgs(byte[] buffer)
        {
            var offset = 0;
            var res = ReadVarBytes(buffer, offset);
            var assetHash = res[0];

            res = ReadVarBytes(buffer, (int)res[1]);
            var nativeAssetHash = res[0];

            return new object[] { assetHash, nativeAssetHash };
        }

        private static byte[] WriteUint255(BigInteger value, byte[] source)
        {
            if (value < 0)
            {
                Runtime.Notify("Value out of range of uint255");
                return source;
            }
            var v = PadRight(value.ToByteArray(), 32);
            return source.Concat(v); // no need to concat length, fix 32 bytes
        }

        private static byte[] WriteVarInt(BigInteger value, byte[] Source)
        {
            if (value < 0)
            {
                return Source;
            }
            else if (value < 0xFD)
            {
                return Source.Concat(value.ToByteArray());
            }
            else if (value <= 0xFFFF) // 0xff, need to pad 1 0x00
            {
                byte[] length = new byte[] { 0xFD };
                var v = PadRight(value.ToByteArray(), 2);
                return Source.Concat(length).Concat(v);
            }
            else if (value <= 0XFFFFFFFF) //0xffffff, need to pad 1 0x00
            {
                byte[] length = new byte[] { 0xFE };
                var v = PadRight(value.ToByteArray(), 4);
                return Source.Concat(length).Concat(v);
            }
            else //0x ff ff ff ff ff, need to pad 3 0x00
            {
                byte[] length = new byte[] { 0xFF };
                var v = PadRight(value.ToByteArray(), 8);
                return Source.Concat(length).Concat(v);
            }
        }

        private static byte[] WriteVarBytes(byte[] value, byte[] Source)
        {
            return WriteVarInt(value.Length, Source).Concat(value);
        }

        // add padding zeros on the right
        private static byte[] PadRight(byte[] value, int length)
        {
            var l = value.Length;
            if (l > length)
                return value.Range(0, length);
            for (int i = 0; i < length - l; i++)
            {
                value = value.Concat(new byte[] { 0x00 });
            }
            return value;
        }
    }
}
