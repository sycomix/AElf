﻿    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using AElf.ChainController;
    using AElf.Execution;
    using AElf.Kernel;
    using AElf.Kernel.Managers;
    using AElf.Kernel.Storages;
    using AElf.SmartContract;
    using Google.Protobuf;
    using ServiceStack;
using    AElf.Common;
    using AElf.Execution.Execution;

namespace AElf.Contracts.SideChain.Tests
    {
        public class MockSetup
        {
            // IncrementId is used to differentiate txn
            // which is identified by From/To/IncrementId
            private static int _incrementId = 0;
            public ulong NewIncrementId()
            {
                var n = Interlocked.Increment(ref _incrementId);
                return (ulong)n;
            }
    
            public Hash ChainId1 { get; } = Hash.FromString("ChainId1");
            public IStateStore StateStore { get; }
            public ISmartContractManager SmartContractManager;
            public ISmartContractService SmartContractService;
            private IFunctionMetadataService _functionMetadataService;
    
            private IChainCreationService _chainCreationService;
    
            private ISmartContractRunnerFactory _smartContractRunnerFactory;
            
            public MockSetup(IStateStore stateStore, IChainCreationService chainCreationService, DataStore dataStore, IChainContextService chainContextService, IFunctionMetadataService functionMetadataService, ISmartContractRunnerFactory smartContractRunnerFactory)
            {
                StateStore = stateStore;
                _chainCreationService = chainCreationService;
                _functionMetadataService = functionMetadataService;
                _smartContractRunnerFactory = smartContractRunnerFactory;
                SmartContractManager = new SmartContractManager(dataStore);
                Task.Factory.StartNew(async () =>
                {
                    await Init();
                }).Unwrap().Wait();
                SmartContractService = new SmartContractService(SmartContractManager, _smartContractRunnerFactory, stateStore, _functionMetadataService);
    
                new ServicePack()
                {
                    ChainContextService = chainContextService,
                    SmartContractService = SmartContractService,
                    ResourceDetectionService = null,
                    StateStore = StateStore
                };
            }
    
            public byte[] SideChainCode
            {
                get
                {
                    byte[] code = null;
                    using (FileStream file = File.OpenRead(Path.GetFullPath("../../../../AElf.Contracts.SideChain/bin/Debug/netstandard2.0/AElf.Contracts.SideChain.dll")))
                    {
                        code = file.ReadFully();
                    }
                    return code;
                }
            }
            
            public byte[] SCZeroContractCode
            {
                get
                {
                    byte[] code = null;
                    using (FileStream file = File.OpenRead(Path.GetFullPath("../../../../AElf.Contracts.Genesis/bin/Debug/netstandard2.0/AElf.Contracts.Genesis.dll")))
                    {
                        code = file.ReadFully();
                    }
                    return code;
                }
            }
            
            private async Task Init()
            {
                var reg1 = new SmartContractRegistration
                {
                    Category = 0,
                    ContractBytes = ByteString.CopyFrom(SideChainCode),
                    ContractHash = Hash.FromRawBytes(SideChainCode),
                    Type = (int)SmartContractType.SideChainContract
                };
                var reg0 = new SmartContractRegistration
                {
                    Category = 0,
                    ContractBytes = ByteString.CopyFrom(SCZeroContractCode),
                    ContractHash = Hash.FromRawBytes(SCZeroContractCode),
                    Type = (int)SmartContractType.BasicContractZero
                };
    
                var chain1 =
                    await _chainCreationService.CreateNewChainAsync(ChainId1,
                        new List<SmartContractRegistration> {reg0, reg1});
            }
            
            public async Task<IExecutive> GetExecutiveAsync(Address address)
            {
                var executive = await SmartContractService.GetExecutiveAsync(address, ChainId1);
                return executive;
            }
        }
    }
