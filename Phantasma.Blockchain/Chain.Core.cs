using System.Collections.Generic;
using System.Linq;
using Phantasma.Blockchain.Contracts;
using Phantasma.Cryptography;
using Phantasma.Numerics;
using Phantasma.Core;
using Phantasma.Core.Log;
using Phantasma.Blockchain.Tokens;
using Phantasma.Blockchain.Contracts.Native;
using Phantasma.Blockchain.Storage;
using Phantasma.VM;
using Phantasma.VM.Utils;
using Phantasma.Core.Types;
using Phantasma.Blockchain.Consensus;
using System;
using Phantasma.IO;
using System.IO;

namespace Phantasma.Blockchain
{
    public class BlockGenerationException : Exception
    {
        public BlockGenerationException(string msg) : base(msg)
        {

        }
    }

    public class InvalidTransactionException : Exception
    {
        public readonly Hash Hash;

        public InvalidTransactionException(Hash hash, string msg) : base(msg)
        {
            this.Hash = hash;
        }
    }

    public partial class Chain: ISerializable
    {
        #region PRIVATE
        private KeyValueStore<Transaction> _transactions;
        private KeyValueStore<Block> _blocks;
        private KeyValueStore<Hash> _transactionBlockMap;
        private KeyValueStore<Epoch> _epochMap;

        private Dictionary<BigInteger, Block> _blockHeightMap = new Dictionary<BigInteger, Block>();

        private Dictionary<Token, BalanceSheet> _tokenBalances = new Dictionary<Token, BalanceSheet>();
        private Dictionary<Token, OwnershipSheet> _tokenOwnerships = new Dictionary<Token, OwnershipSheet>();
        private Dictionary<Token, SupplySheet> _tokenSupplies = new Dictionary<Token, SupplySheet>();

        private Dictionary<Hash, StorageChangeSetContext> _blockChangeSets = new Dictionary<Hash, StorageChangeSetContext>();

        private Dictionary<string, SmartContract> _contracts = new Dictionary<string, SmartContract>();
        private Dictionary<string, ExecutionContext> _contractContexts = new Dictionary<string, ExecutionContext>();
        
        private int _level;
        #endregion

        #region PUBLIC
        public static readonly uint InitialHeight = 1;

        public int Level => _level;

        public Chain ParentChain { get; private set; }
        public Block ParentBlock { get; private set; }
        public Nexus Nexus { get; private set; }

        public string Name { get; private set; }
        public Address Address { get; private set; }

        public Epoch CurrentEpoch { get; private set; }

        public uint BlockHeight => (uint)_blocks.Count;
       
        public Block LastBlock { get; private set; }

        public readonly Logger Log;

        public StorageContext Storage { get; private set; }

        public uint TransactionCount => _transactions.Count;

        public bool IsRoot => this.ParentChain == null;
        #endregion

        // required for serialization
        public Chain()
        {

        }

        public Chain(Nexus nexus, string name, IEnumerable<SmartContract> contracts, Logger log = null, Chain parentChain = null, Block parentBlock = null)
        {
            Throw.IfNull(nexus, "nexus required");
            Throw.If(contracts == null || !contracts.Any(), "contracts required");

            if (parentChain != null)
            {
                Throw.IfNull(parentBlock, "parent block required");
                Throw.IfNot(nexus.ContainsChain(parentChain), "invalid chain");
                //Throw.IfNot(parentChain.ContainsBlock(parentBlock), "invalid block"); // TODO should this be required? 
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(name.ToLower());
            var hash = CryptoExtensions.SHA256(bytes);

            this.Address = new Address(hash);

            // init stores
            _transactions = new KeyValueStore<Transaction>(this.Address, "txs", KeyStoreDataSize.Medium, nexus.CacheSize);
            _blocks = new KeyValueStore<Block>(this.Address, "blocks", KeyStoreDataSize.Medium, nexus.CacheSize);
            _transactionBlockMap = new KeyValueStore<Hash>(this.Address, "txbk", KeyStoreDataSize.Small, nexus.CacheSize);
            _epochMap = new KeyValueStore<Epoch>(this.Address, "epoch", KeyStoreDataSize.Medium, nexus.CacheSize);

            foreach (var contract in contracts)
            {
                if (this._contracts.ContainsKey(contract.Name))
                {
                    throw new ChainException("Duplicated contract name: " + contract.Name);
                }

                this._contracts[contract.Name] = contract;
                this._contractContexts[contract.Name] = new NativeExecutionContext(contract);
            }

            this.Name = name;
            this.Nexus = nexus;

            this.ParentChain = parentChain;
            this.ParentBlock = parentBlock;

            if (nexus.CacheSize == -1)
            {
                this.Storage = new MemoryStorageContext();
            }
            else
            {
                this.Storage = new DiskStorageContext(this.Address, "data", KeyStoreDataSize.Medium);
            }

            this.Log = Logger.Init(log);

            if (parentChain != null)
            {
                parentChain._childChains[name] = this;
                _level = ParentChain.Level + 1;
            }
            else
            {
                _level = 1;
            }
        }

        public override string ToString()
        {
            return $"{Name} ({Address})";
        }

        public bool ContainsBlock(Hash hash)
        {
            if (hash == null)
            {
                return false;
            }

            return _blocks.ContainsKey(hash);
        }

        public IEnumerable<Transaction> GetBlockTransactions(Block block)
        {
            return block.TransactionHashes.Select(hash => FindTransactionByHash(hash));
        }

        public void AddBlock(Block block, IEnumerable<Transaction> transactions)
        {
            /*if (CurrentEpoch != null && CurrentEpoch.IsSlashed(Timestamp.Now))
            {
                return false;
            }*/

            if (LastBlock != null)
            {
                if (LastBlock.Height != block.Height - 1)
                {
                    throw new BlockGenerationException($"height of block should be {LastBlock.Height + 1}");
                }

                if (block.PreviousHash != LastBlock.Hash)
                {
                    throw new BlockGenerationException($"previous hash should be {LastBlock.PreviousHash}");
                }
            }

            var inputHashes = new HashSet<Hash>(transactions.Select(x => x.Hash));
            foreach (var hash in block.TransactionHashes)
            {
                if (!inputHashes.Contains(hash))
                {
                    throw new BlockGenerationException($"missing in inputs transaction with hash {hash}");
                }
            }

            var outputHashes = new HashSet<Hash>(block.TransactionHashes);
            foreach (var tx in transactions)
            {
                if (!outputHashes.Contains(tx.Hash))
                {
                    throw new BlockGenerationException($"missing in outputs transaction with hash {tx.Hash}");
                }
            }

            foreach (var tx in transactions)
            {
                if (!tx.IsValid(this))
                {
                    throw new InvalidTransactionException(tx.Hash, $"invalid transaction with hash {tx.Hash}");
                }
            }

            var changeSet = new StorageChangeSetContext(this.Storage);

            foreach (var  tx in transactions)
            {
                byte[] result;
                if (tx.Execute(this, block, changeSet, block.Notify, out result))
                {
                    if (result != null)
                    {
                        block.SetResultForHash(tx.Hash, result);
                    }
                }
                else
                { 
                    throw new InvalidTransactionException(tx.Hash, $"transaction execution failed with hash {tx.Hash}");
                }
            }

            // from here on, the block is accepted
            _blockHeightMap[block.Height] = block;
            _blocks[block.Hash] = block;
            _blockChangeSets[block.Hash] = changeSet;

            changeSet.Execute();

            if (CurrentEpoch == null)
            {
                GenerateEpoch();
            }

            CurrentEpoch.AddBlockHash(block.Hash);
            CurrentEpoch.UpdateHash();

            LastBlock = block;

            foreach (Transaction tx in transactions)
            {
                _transactions[tx.Hash] = tx;
                _transactionBlockMap[tx.Hash] = block.Hash;
            }

            Nexus.PluginTriggerBlock(this, block);
        }

        private Dictionary<string, Chain> _childChains = new Dictionary<string, Chain>();
        public IEnumerable<Chain> ChildChains => _childChains.Values;

        public Chain FindChildChain(Address address)
        {
            Throw.If(address == Address.Null, "invalid address");

            foreach (var childChain in _childChains.Values)
            {
                if (childChain.Address == address)
                {
                    return childChain;
                }
            }

            foreach (var childChain in _childChains.Values)
            {
                var result = childChain.FindChildChain(address);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        public Chain GetRoot()
        {
            var result = this;
            while (result.ParentChain != null)
            {
                result = result.ParentChain;
            }

            return result;
        }

        public bool ContainsTransaction(Hash hash)
        {
            return _transactions.ContainsKey(hash);
        }

        public Transaction FindTransactionByHash(Hash hash)
        {
            return _transactions.ContainsKey(hash) ? _transactions[hash] : null;
        }

        public Block FindTransactionBlock(Transaction tx)
        {
            return FindTransactionBlock(tx.Hash);
        }

        public Block FindTransactionBlock(Hash hash)
        {
            if (_transactionBlockMap.ContainsKey(hash))
            {
                var blockHash = _transactionBlockMap[hash];
                return FindBlockByHash(blockHash);
            }

            return null;
        }

        public Block FindBlockByHash(Hash hash)
        {
            return _blocks.ContainsKey(hash) ? _blocks[hash] : null;
        }

        public Block FindBlockByHeight(BigInteger height)
        {
            return _blockHeightMap.ContainsKey(height) ? _blockHeightMap[height] : null;
        }

        public BalanceSheet GetTokenBalances(Token token)
        {
            Throw.If(!token.Flags.HasFlag(TokenFlags.Fungible), "should be fungible");

            if (_tokenBalances.ContainsKey(token))
            {
                return _tokenBalances[token];
            }

            var sheet = new BalanceSheet(token.Symbol, this.Storage);
            _tokenBalances[token] = sheet;
            return sheet;
        }

        internal void InitSupplySheet(Token token, BigInteger maxSupply)
        {
            Throw.If(!token.IsCapped, "should be capped");
            Throw.If(_tokenSupplies.ContainsKey(token), "supply sheet already created");

            var sheet = new SupplySheet(0, 0, maxSupply);
            _tokenSupplies[token] = sheet;
        }

        internal SupplySheet GetTokenSupplies(Token token)
        {
            Throw.If(!token.IsCapped, "should be capped");

            if (_tokenSupplies.ContainsKey(token))
            {
                return _tokenSupplies[token];
            }

            Throw.If(this.ParentChain == null, "supply sheet not created");

            var parentSupplies = this.ParentChain.GetTokenSupplies(token);

            var sheet = new SupplySheet(parentSupplies.LocalBalance, 0, token.MaxSupply);
            _tokenSupplies[token] = sheet;
            return sheet;
        }

        public OwnershipSheet GetTokenOwnerships(Token token)
        {
            Throw.If(token.Flags.HasFlag(TokenFlags.Fungible), "cannot be fungible");

            if (_tokenOwnerships.ContainsKey(token))
            {
                return _tokenOwnerships[token];
            }

            var sheet = new OwnershipSheet(token.Symbol);
            _tokenOwnerships[token] = sheet;
            return sheet;
        }

        public BigInteger GetTokenBalance(Token token, Address address)
        {
            if (token.Flags.HasFlag(TokenFlags.Fungible))
            {
                var balances = GetTokenBalances(token);
                return balances.Get(Storage, address);
            }
            else
            {
                var ownerships = GetTokenOwnerships(token);
                var items = ownerships.Get(this.Storage, address);
                return items.Count();
            }

            /*            var contract = this.FindContract(token);
                        Throw.IfNull(contract, "contract not found");

                        var tokenABI = Chain.FindABI(NativeABI.Token);
                        Throw.IfNot(contract.ABI.Implements(tokenABI), "invalid contract");

                        var balance = (BigInteger)tokenABI["BalanceOf"].Invoke(contract, account);
                        return balance;*/
        }

        public Address GetTokenOwner(Token token, BigInteger tokenID)
        {
            Throw.If(token.IsFungible, "non fungible required");

            var ownerships = GetTokenOwnerships(token);
            return ownerships.GetOwner(this.Storage, tokenID);
        }

        public IEnumerable<BigInteger> GetOwnedTokens(Token token, Address address)
        {
            var ownership = GetTokenOwnerships(token);
            return ownership.Get(this.Storage, address);
        }

        public static bool ValidateName(string name)
        {
            if (name == null)
            {
                return false;
            }

            if (name.Length < 3 || name.Length >= 20)
            {
                return false;
            }

            int index = 0;
            while (index < name.Length)
            {
                var c = (int)name[index];
                index++;

                if (c >= 97 && c <= 122) continue; // lowercase allowed
                if (c == 95) continue; // underscore allowed
                if (c >= 48 && c <= 57) continue; // numbers allowed

                return false;
            }

            return true;
        }

        /// <summary>
        /// Deletes all blocks starting at the specified hash.
        /// </summary>
        public void DeleteBlocks(Hash targetHash)
        {
            var targetBlock = FindBlockByHash(targetHash);
            Throw.IfNull(targetBlock, nameof(targetBlock));

            var currentBlock = this.LastBlock;
            while (true)
            {
                Throw.IfNull(currentBlock, nameof(currentBlock));

                var changeSet = _blockChangeSets[currentBlock.Hash];
                changeSet.Undo();

                _blockChangeSets.Remove(currentBlock.Hash);
                _blockHeightMap.Remove(currentBlock.Height);
                _blocks.Remove(currentBlock.Hash);

                currentBlock = FindBlockByHash(currentBlock.PreviousHash);
                this.LastBlock = currentBlock;

                if (currentBlock.PreviousHash == targetHash)
                {
                    break;
                }
            }
        }

        public T FindContract<T>(string contractName) where T: SmartContract
        {
            Throw.IfNullOrEmpty(contractName, nameof(contractName));

            if (_contracts.ContainsKey(contractName))
            {
                return (T)_contracts[contractName];
            }

            return null;
        }

        internal ExecutionContext GetContractContext(SmartContract contract)
        {
            if (_contractContexts.ContainsKey(contract.Name))
            {
                return _contractContexts[contract.Name];
            }

            return null;
        }

        public object InvokeContract(string contractName, string methodName, params object[] args)
        {
            var contract = FindContract<SmartContract>(contractName);
            Throw.IfNull(contract, nameof(contract));

            var script = ScriptUtils.BeginScript().CallContract(contractName, methodName, args).EndScript();
            var changeSet = new StorageChangeSetContext(this.Storage);
            var vm = new RuntimeVM(script, this, this.LastBlock, null, changeSet, true);

            contract.SetRuntimeData(vm);

            var state = vm.Execute();

            if (state != ExecutionState.Halt)
            {
                throw new ChainException($"Invocation of method '{methodName}' of contract '{contractName}' failed with state: " + state);
            }

            if (vm.Stack.Count == 0)
            {
                throw new ChainException($"No result, vm stack is empty");
            }

            var result = vm.Stack.Pop();

            return result.ToObject();
        }

        #region FEES 
        public BigInteger GetBlockReward(Block block)
        {
            BigInteger total = 0;
            foreach (var hash in block.TransactionHashes)
            {
                var events = block.GetEventsForTransaction(hash);
                foreach (var evt in events)
                {
                    if (evt.Kind == EventKind.GasPayment)
                    {
                        var gasInfo = evt.GetContent<GasEventData>();
                        total += gasInfo.price * gasInfo.amount;
                    }
                }
            }

            return total;
        }

        public BigInteger GetTransactionFee(Transaction tx)
        {
            Throw.IfNull(tx, nameof(tx));
            return GetTransactionFee(tx.Hash);
        }

        public BigInteger GetTransactionFee(Hash hash)
        {
            Throw.IfNull(hash, nameof(hash));

            BigInteger fee = 0;

            var block = FindTransactionBlock(hash);
            Throw.IfNull(block, nameof(block));

            var events = block.GetEventsForTransaction(hash);
            foreach (var evt in events)
            {
                if (evt.Kind == EventKind.GasPayment)
                {
                    var info = evt.GetContent<GasEventData>();
                    fee += info.amount * info.price;
                }
            }

            return fee;
        }
        #endregion

        #region EPOCH
        public bool IsCurrentValidator(Address address)
        {
            if (CurrentEpoch != null)
            {
                return CurrentEpoch.ValidatorAddress == address;
            }

            var firstValidator = Nexus.GetValidatorByIndex(0);
            return address == firstValidator;
        }

        private void GenerateEpoch()
        {
            Address nextValidator;

            uint epochIndex;

            if (CurrentEpoch != null)
            {
                epochIndex = CurrentEpoch.Index + 1;

                var currentIndex = Nexus.GetIndexOfValidator(CurrentEpoch.ValidatorAddress);
                currentIndex++;

                var validatorCount = Nexus.GetValidatorCount();

                if (currentIndex >= validatorCount)
                {
                    currentIndex = 0;
                }

                nextValidator = Nexus.GetValidatorByIndex(currentIndex);
            }
            else
            {
                epochIndex = 0;
                nextValidator = Nexus.GetValidatorByIndex(0);
            }

            var epoch = new Epoch(epochIndex, Timestamp.Now, nextValidator, CurrentEpoch != null ? CurrentEpoch.Hash : Hash.Null);

            CurrentEpoch = epoch;
        }
        #endregion


        public void SerializeData(BinaryWriter writer)
        {
            throw new NotImplementedException();
        }

        public void UnserializeData(BinaryReader reader)
        {
            throw new NotImplementedException();
        }
    }
}
