﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Threading.Tasks;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    public interface ISharedMemoryManager
    {
        Task<SharedMemoryMetadata> PutObjectAsync(object input);

        Task<byte[]> GetBytesAsync(string mapName, long offset, long count);

        Task<string> GetStringAsync(string mapName, long offset, long count);

        bool IsSupported(object input);

        void AddSharedMemoryMapForInvocation(string invocationId, string mapName);

        bool TryFreeSharedMemoryMapsForInvocation(string invocationId);

        bool TryFreeSharedMemoryMap(string mapName);
    }
}
