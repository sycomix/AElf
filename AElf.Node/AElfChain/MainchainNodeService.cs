using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AElf.ChainController;
using AElf.ChainController.EventMessages;
using AElf.Common;
using AElf.Common.Attributes;
using AElf.Common.Enums;
using AElf.Configuration;
using AElf.Configuration.Config.Consensus;
using AElf.Kernel;
using AElf.Kernel.Node;
using AElf.Kernel.Storages;
using AElf.Miner.EventMessages;
using AElf.Miner.Miner;
using AElf.Miner.TxMemPool;
using AElf.Node.EventMessages;
using AElf.Synchronization.BlockExecution;
using AElf.Synchronization.BlockSynchronization;
using AElf.Synchronization.EventMessages;
using Easy.MessageHub;
using Google.Protobuf;
using NLog;
using ServiceStack;

namespace AElf.Node.AElfChain
{
    // ReSharper disable InconsistentNaming
    [LoggerName("Node")]
    public class MainchainNodeService : INodeService
    {
        private readonly ILogger _logger;
        private readonly ITxHub _txHub;
        private readonly IStateStore _stateStore;
        private readonly IMiner _miner;
        private readonly IP2P _p2p;
        private readonly IBlockValidationService _blockValidationService;
        private readonly IChainContextService _chainContextService;
        private readonly IChainService _chainService;
        private readonly IChainCreationService _chainCreationService;
        private readonly IBlockSynchronizer _blockSynchronizer;
        private readonly IBlockExecutor _blockExecutor;

        private IBlockChain _blockChain;
        private IConsensus _consensus;

        // TODO temp solution because to get the dlls we need the launchers directory (?)
        private string _assemblyDir;

        public MainchainNodeService(
            IStateStore stateStore,
            ITxHub hub,
            IBlockValidationService blockValidationService,
            IChainContextService chainContextService,
            IChainCreationService chainCreationService,
            IBlockSynchronizer blockSynchronizer,
            IChainService chainService,
            IMiner miner,
            IP2P p2p,
            IBlockExecutor blockExecutor,
            ILogger logger)
        {
            _stateStore = stateStore;
            _chainCreationService = chainCreationService;
            _chainService = chainService;
            _txHub = hub;
            _logger = logger;
            _blockExecutor = blockExecutor;
            _miner = miner;
            _p2p = p2p;
            _blockValidationService = blockValidationService;
            _chainContextService = chainContextService;
            _blockSynchronizer = blockSynchronizer;
        }

        #region Genesis Contracts

        private byte[] TokenGenesisContractCode
        {
            get
            {
                var contractZeroDllPath = Path.Combine(_assemblyDir, $"{GlobalConfig.GenesisTokenContractAssemblyName}.dll");

                byte[] code;
                using (var file = File.OpenRead(Path.GetFullPath(contractZeroDllPath)))
                {
                    code = file.ReadFully();
                }

                return code;
            }
        }

        private byte[] ConsensusGenesisContractCode
        {
            get
            {
                var contractZeroDllPath = Path.Combine(_assemblyDir, $"{GlobalConfig.GenesisConsensusContractAssemblyName}.dll");

                byte[] code;
                using (var file = File.OpenRead(Path.GetFullPath(contractZeroDllPath)))
                {
                    code = file.ReadFully();
                }

                return code;
            }
        }

        private byte[] BasicContractZero
        {
            get
            {
                var contractZeroDllPath = Path.Combine(_assemblyDir, $"{GlobalConfig.GenesisSmartContractZeroAssemblyName}.dll");

                byte[] code;
                using (var file = File.OpenRead(Path.GetFullPath(contractZeroDllPath)))
                {
                    code = file.ReadFully();
                }

                return code;
            }
        }

        private byte[] SideChainGenesisContractZero
        {
            get
            {
                var contractZeroDllPath = Path.Combine(_assemblyDir, $"{GlobalConfig.GenesisSideChainContractAssemblyName}.dll");

                byte[] code;
                using (var file = File.OpenRead(Path.GetFullPath(contractZeroDllPath)))
                {
                    code = file.ReadFully();
                }

                return code;
            }
        }

        #endregion

        public void Initialize(NodeConfiguration conf)
        {
            _assemblyDir = conf.LauncherAssemblyLocation;
            _blockChain = _chainService.GetBlockChain(Hash.LoadHex(NodeConfig.Instance.ChainId));
            NodeConfig.Instance.ECKeyPair = conf.KeyPair;

            SetupConsensus();

            MessageHub.Instance.Subscribe<TxReceived>(async inTx => { await _txHub.AddTransactionAsync(inTx.Transaction); });

            MessageHub.Instance.Subscribe<UpdateConsensus>(option =>
            {
                if (option == UpdateConsensus.Update)
                {
                    _logger?.Trace("Will update consensus.");
                    _consensus?.Update();
                }

                if (option == UpdateConsensus.Dispose)
                {
                    _logger?.Trace("Will stop mining.");
                    _consensus?.Stop();
                }
            });

            MessageHub.Instance.Subscribe<SyncStateChanged>(inState =>
            {
                if (inState.IsSyncing)
                {
                    _logger?.Trace("Will hang on mining due to starting syncing.");
                    _consensus?.Hang();
                }
                else
                {
                    _logger?.Trace("Will start / recover mining.");
                    _consensus?.Start();
                }
            });

            MessageHub.Instance.Subscribe<ConsensusGenerated>(inState =>
            {
                if (inState.IsGenerated)
                {
                    _logger?.Trace("Will hang on mining due to starting syncing.");
                    _consensus?.Hang();
                }
                else
                {
                    _logger?.Trace("Will start / recover mining.");
                    _consensus?.Start();
                }
            });
            _txHub.Initialize();
        }

        public bool Start()
        {
            if (string.IsNullOrWhiteSpace(NodeConfig.Instance.ChainId))
            {
                _logger?.Error("No chain id.");
                return false;
            }

            _logger?.Info($"Chain Id = {NodeConfig.Instance.ChainId}");

            #region setup

            try
            {
                LogGenesisContractInfo();

                var curHash = _blockChain.GetCurrentBlockHashAsync().Result;

                var chainExists = curHash != null && !curHash.Equals(Hash.Genesis);

                if (!chainExists)
                {
                    // Creation of the chain if it doesn't already exist
                    CreateNewChain(TokenGenesisContractCode, ConsensusGenesisContractCode, BasicContractZero,
                        SideChainGenesisContractZero);
                }
            }
            catch (Exception e)
            {
                _logger?.Error(e, $"Could not create the chain : {NodeConfig.Instance.ChainId}.");
            }

            #endregion setup

            #region start

            _txHub.Start();
            _blockExecutor.Init();

            if (NodeConfig.Instance.IsMiner)
            {
                _miner.Init();
                _logger?.Debug($"Coinbase = {_miner.Coinbase.DumpHex()}");
            }

            // todo maybe move
            Task.Run(async () => await _p2p.ProcessLoop()).ConfigureAwait(false);

            Thread.Sleep(1000);

            if (NodeConfig.Instance.ConsensusInfoGenerator)
            {
                StartMining();
                // Start directly.
                _consensus?.Start();
            }

            MessageHub.Instance.Subscribe<BlockReceived>(async inBlock => { await _blockSynchronizer.ReceiveBlock(inBlock.Block); });

            MessageHub.Instance.Subscribe<BlockMined>(inBlock => { _blockSynchronizer.AddMinedBlock(inBlock.Block); });

            #endregion start

            MessageHub.Instance.Publish(new ChainInitialized(null));

            return true;
        }

        public void Stop()
        {
            //todo
        }

        public bool IsDPoSAlive()
        {
            return _consensus.IsAlive();
        }

        // TODO: 
        public bool IsForked()
        {
            return false;
        }

        #region private methods

        private Address GetGenesisContractHash(SmartContractType contractType)
        {
            return _chainCreationService.GenesisContractHash(Hash.LoadHex(NodeConfig.Instance.ChainId), contractType);
        }

        private void LogGenesisContractInfo()
        {
            var genesis = GetGenesisContractHash(SmartContractType.BasicContractZero);
            _logger?.Debug($"Genesis contract address = {genesis.DumpHex()}");

            var tokenContractAddress = GetGenesisContractHash(SmartContractType.TokenContract);
            _logger?.Debug($"Token contract address = {tokenContractAddress.DumpHex()}");

            var consensusAddress = GetGenesisContractHash(SmartContractType.AElfDPoS);
            _logger?.Debug($"DPoS contract address = {consensusAddress.DumpHex()}");

            var sidechainContractAddress = GetGenesisContractHash(SmartContractType.SideChainContract);
            _logger?.Debug($"SideChain contract address = {sidechainContractAddress.DumpHex()}");
        }

        private void CreateNewChain(byte[] tokenContractCode, byte[] consensusContractCode, byte[] basicContractZero,
            byte[] sideChainGenesisContractCode)
        {
            var tokenCReg = new SmartContractRegistration
            {
                Category = 0,
                ContractBytes = ByteString.CopyFrom(tokenContractCode),
                ContractHash = Hash.FromRawBytes(tokenContractCode),
                Type = (int) SmartContractType.TokenContract
            };

            var consensusCReg = new SmartContractRegistration
            {
                Category = 0,
                ContractBytes = ByteString.CopyFrom(consensusContractCode),
                ContractHash = Hash.FromRawBytes(consensusContractCode),
                Type = (int) SmartContractType.AElfDPoS
            };

            var basicReg = new SmartContractRegistration
            {
                Category = 0,
                ContractBytes = ByteString.CopyFrom(basicContractZero),
                ContractHash = Hash.FromRawBytes(basicContractZero),
                Type = (int) SmartContractType.BasicContractZero
            };

            var sideChainCReg = new SmartContractRegistration
            {
                Category = 0,
                ContractBytes = ByteString.CopyFrom(sideChainGenesisContractCode),
                ContractHash = Hash.FromRawBytes(sideChainGenesisContractCode),
                Type = (int) SmartContractType.SideChainContract
            };
            var res = _chainCreationService.CreateNewChainAsync(Hash.LoadHex(NodeConfig.Instance.ChainId),
                new List<SmartContractRegistration> {basicReg, tokenCReg, consensusCReg, sideChainCReg}).Result;

            _logger?.Debug($"Genesis block hash = {res.GenesisBlockHash.DumpHex()}");
        }

        private void SetupConsensus()
        {
            if (_consensus != null)
            {
                _logger?.Trace("Consensus has already initialized.");
                return;
            }

            switch (ConsensusConfig.Instance.ConsensusType)
            {
                case ConsensusType.AElfDPoS:
                    _consensus = new DPoS(_stateStore, _txHub, _miner, _chainService);
                    break;

                case ConsensusType.PoTC:
                    _consensus = new PoTC(_miner, _txHub);
                    break;

                case ConsensusType.SingleNode:
                    _consensus = new StandaloneNodeConsensusPlaceHolder();
                    break;
            }
        }

        private void StartMining()
        {
            if (NodeConfig.Instance.IsMiner)
            {
                SetupConsensus();
                _consensus?.Start();
            }
        }

        #endregion private methods

        public int GetCurrentHeight()
        {
            int height = 1;

            try
            {
                height = (int) _blockChain.GetCurrentBlockHeightAsync().Result;
            }
            catch (Exception e)
            {
                _logger?.Error(e, "Exception while getting chain height.");
            }

            return height;
        }
    }
}