// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer
{
    public class SharedMemoryManager : ISharedMemoryManager
    {
        private readonly ILoggerFactory _loggerFactory;

        private readonly ILogger _logger;

        private readonly IMemoryMappedFileAccessor _mapAccessor;

        /// <summary>
        /// Mapping of invocation ID to list of names of shared memory maps allocated for that invocation.
        /// </summary>
        private readonly ConcurrentDictionary<string, IList<string>> _invocationSharedMemoryMaps;

        private readonly ConcurrentDictionary<string, SharedMemoryMap> _allocatedSharedMemoryMaps;

        public SharedMemoryManager(ILoggerFactory loggerFactory, IMemoryMappedFileAccessor mapAccessor)
        {
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<SharedMemoryManager>();
            _mapAccessor = mapAccessor;
            _allocatedSharedMemoryMaps = new ConcurrentDictionary<string, SharedMemoryMap>();
            _invocationSharedMemoryMaps = new ConcurrentDictionary<string, IList<string>>();
        }

        /// <summary>
        /// Writes the given data into a <see cref="MemoryMappedFile"/>.
        /// Note:
        /// Tracks the reference to the <see cref="SharedMemoryMap"/> after creating it and does not close it.
        /// </summary>
        /// <param name="content">Content to write into shared memory.</param>
        /// <returns>Name of the <see cref="MemoryMappedFile"/> into which data is written if
        /// successful, <see cref="null"/> otherwise.</returns>
        private async Task<SharedMemoryMetadata> PutBytesAsync(byte[] content)
        {
            // Generate name of shared memory map to write content into
            string mapName = Guid.NewGuid().ToString();

            // Create shared memory map which can hold the content
            long contentSize = content.Length;
            SharedMemoryMap sharedMemoryMap = Create(mapName, contentSize);

            // Ensure the shared memory map was created
            if (sharedMemoryMap != null)
            {
                // Write content into shared memory map
                long bytesWritten = await sharedMemoryMap.PutBytesAsync(content);

                // Ensure that the entire content has been written into the shared memory map
                if (bytesWritten == contentSize)
                {
                    // Track the shared memory map (to keep a reference open so that the OS does not free the memory)
                    if (_allocatedSharedMemoryMaps.TryAdd(mapName, sharedMemoryMap))
                    {
                        // Respond back with metadata about the created and written shared memory map
                        SharedMemoryMetadata response = new SharedMemoryMetadata
                        {
                            Name = mapName,
                            Count = contentSize
                        };
                        return response;
                    }
                    else
                    {
                        sharedMemoryMap.Dispose();
                    }
                }
            }

            _logger.LogError($"Cannot write content into shared memory");
            return null;
        }

        private Task<SharedMemoryMetadata> PutStringAsync(string content)
        {
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);
            return PutBytesAsync(contentBytes);
        }

        public Task<SharedMemoryMetadata> PutObjectAsync(object input)
        {
            if (input is byte[] arr)
            {
                return PutBytesAsync(arr);
            }
            else if (input is string str)
            {
                return PutStringAsync(str);
            }
            else
            {
                return null;
            }
        }

        public void AddSharedMemoryMapForInvocation(string invocationId, string mapName)
        {
            IList<string> sharedMemoryMaps = _invocationSharedMemoryMaps.GetOrAdd(invocationId, new List<string>());
            sharedMemoryMaps.Add(mapName);
        }

        /// <summary>
        /// Reads data from the <see cref="MemoryMappedFile"/> with the given name.
        /// Note:
        /// Closes the reference to the <see cref="SharedMemoryMap"/> after reading.
        /// </summary>
        /// <param name="mapName">Name of the <see cref="MemoryMappedFile"/> to read from.</param>
        /// <param name="offset">Offset to start reading data from in the
        /// <see cref="MemoryMappedFile"/>.</param>
        /// <param name="count">Number of bytes to read from, starting from the offset, in the
        /// <see cref="MemoryMappedFile"/>.</param>
        /// <returns>Data read as <see cref="byte[]"/> if successful, <see cref="null"/> otherwise.
        /// </returns>
        public async Task<byte[]> GetBytesAsync(string mapName, long offset, long count)
        {
            SharedMemoryMap sharedMemoryMap = Open(mapName);

            try
            {
                byte[] content = await sharedMemoryMap.GetBytesAsync(offset, count);

                if (content != null)
                {
                    return content;
                }
                else
                {
                    _logger.LogError($"Cannot read content from MemoryMappedFile: {mapName}");
                    return null;
                }
            }
            finally
            {
                sharedMemoryMap.Dispose(deleteFile: false);
            }
        }

        public async Task<string> GetStringAsync(string mapName, long offset, long count)
        {
            byte[] contentBytes = await GetBytesAsync(mapName, offset, count);
            return Encoding.UTF8.GetString(contentBytes);
        }

        public bool TryFreeSharedMemoryMapsForInvocation(string invocationId)
        {
            bool freedAll = true;
            if (_invocationSharedMemoryMaps.TryRemove(invocationId, out IList<string> mapNames))
            {
                foreach (string mapName in mapNames)
                {
                    if (!TryFreeSharedMemoryMap(mapName))
                    {
                        freedAll = false;
                    }
                }
            }

            return freedAll;
        }

        public bool TryFreeSharedMemoryMap(string mapName)
        {
            if (_allocatedSharedMemoryMaps.TryRemove(mapName, out SharedMemoryMap sharedMemoryMap))
            {
                sharedMemoryMap.Dispose();
                return true;
            }
            else
            {
                _logger.LogWarning($"Cannot find SharedMemoryMap: {mapName}");
                return false;
            }
        }

        public bool IsSupported(object input)
        {
            if (input is byte[] arr)
            {
                if (arr.Length >= SharedMemoryConstants.MinObjectSizeForSharedMemoryTransfer)
                {
                    return true;
                }
            }
            else if (input is string str)
            {
                if (str.Length >= SharedMemoryConstants.MinObjectSizeForSharedMemoryTransfer)
                {
                    return true;
                }
            }

            return false;
        }

        private SharedMemoryMap Create(string mapName, long contentSize)
        {
            long size = contentSize + SharedMemoryConstants.HeaderTotalBytes;
            if (_mapAccessor.TryCreate(mapName, size, out MemoryMappedFile mmf))
            {
                return new SharedMemoryMap(_loggerFactory, _mapAccessor, mapName, mmf);
            }
            else
            {
                return null;
            }
        }

        private SharedMemoryMap Open(string mapName)
        {
            if (_mapAccessor.TryOpen(mapName, out MemoryMappedFile mmf))
            {
                return new SharedMemoryMap(_loggerFactory, _mapAccessor, mapName, mmf);
            }
            else
            {
                return null;
            }
        }
    }
}
