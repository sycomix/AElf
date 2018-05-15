﻿using System.Threading.Tasks;

namespace AElf.Kernel.Managers
{
    public interface IChainManager
    {
        Task AppendBlockToChainAsync(Hash chainId, IBlock block);
        Task<IChain> GetChainAsync(Hash id);
        Task<IChain> AddChainAsync(Hash chainId, Hash genesisBlockHash);
        
        /// <summary>
        /// get height for one chain
        /// </summary>
        /// <param name="chainId"></param>
        /// <returns></returns>
        Task<ulong> GetChainCurrentHeight(Hash chainId);

        /// <summary>
        /// set height for one chain
        /// </summary>
        /// <param name="chainId"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        Task SetChainCurrentHeight(Hash chainId, ulong height);
        
        /// <summary>
        /// get last block hash for one chain
        /// </summary>
        /// <param name="chainId"></param>
        /// <returns></returns>
        Task<Hash> GetChainLastBlockHash(Hash chainId);

        /// <summary>
        /// set height for one chain
        /// </summary>
        /// <param name="chainId"></param>
        /// <param name="blockHash"></param>
        /// <returns></returns>
        Task SetChainLastBlockHash(Hash chainId, Hash blockHash);
    }
} 