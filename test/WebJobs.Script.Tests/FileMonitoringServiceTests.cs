﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WebJobs.Script.Tests;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class FileMonitoringServiceTests
    {
        [Theory]
        [InlineData(@"C:\Functions\Scripts\Shared\Test.csx", "Shared")]
        [InlineData(@"C:\Functions\Scripts\Shared\Sub1\Sub2\Test.csx", "Shared")]
        [InlineData(@"C:\Functions\Scripts\Shared", "Shared")]
        public static void GetRelativeDirectory_ReturnsExpectedDirectoryName(string path, string expected)
        {
            Assert.Equal(expected, FileMonitoringService.GetRelativeDirectory(path, @"C:\Functions\Scripts"));
        }

        [Theory]
        [InlineData(@"NonExistentPath")]
        [InlineData(null)]
        public void InitializesEmptyOrMissingDirectorySnapshot(string rootScriptPath)
        {
            using (var directory = new TempDirectory())
            {
                // Setup
                string tempDir = directory.Path;
                Directory.CreateDirectory(Path.Combine(tempDir, "Host"));

                var fileMonitoringService = GetFileMonitoringService(tempDir, rootScriptPath);
                Assert.False(fileMonitoringService.GetDirectorySnapshot().IsDefault);
                Assert.True(fileMonitoringService.GetDirectorySnapshot().IsEmpty);
            }
        }

        [Fact]
        public void InitializesGetDirectorySnapshot()
        {
            using (var directory = new TempDirectory())
            {
                string tempDir = directory.Path;
                Directory.CreateDirectory(Path.Combine(tempDir, "Host"));
                var fileMonitoringService = GetFileMonitoringService(tempDir, tempDir);
                Assert.Equal(fileMonitoringService.GetDirectorySnapshot().Length, 1);
                Assert.Equal(fileMonitoringService.GetDirectorySnapshot()[0], Path.Combine(tempDir, "Host"));
            }
        }

        [Fact]
        public static async Task HostRestarts_OnWatchFilesChange()
        {
            using (var directory = new TempDirectory())
            {
                // Setup
                string tempDir = directory.Path;
                Directory.CreateDirectory(Path.Combine(tempDir, "Host"));
                File.Create(Path.Combine(tempDir, "my_watched_file.txt"));
                File.Create(Path.Combine(tempDir, "my_ignored_file.txt"));

                var jobHostOptions = new ScriptJobHostOptions
                {
                    RootLogPath = tempDir,
                    RootScriptPath = tempDir,
                    FileWatchingEnabled = true,
                    WatchFiles = { "my_watched_file.txt" }
                };

                var loggerFactory = new LoggerFactory();
                var mockApplicationLifetime = new Mock<IApplicationLifetime>();
                var mockScriptHostManager = new Mock<IScriptHostManager>();
                var mockEventManager = new ScriptEventManager();
                var environment = new TestEnvironment();

                // Act
                FileMonitoringService fileMonitoringService = new FileMonitoringService(new OptionsWrapper<ScriptJobHostOptions>(jobHostOptions),
                    loggerFactory, mockEventManager, mockApplicationLifetime.Object, mockScriptHostManager.Object, environment);
                await fileMonitoringService.StartAsync(new CancellationToken(canceled: false));

                var ignoredFileEventArgs = new FileSystemEventArgs(WatcherChangeTypes.Created, tempDir, "my_ignored_file.txt");
                FileEvent ignoredFileEvent = new FileEvent("ScriptFiles", ignoredFileEventArgs);

                var watchedFileEventArgs = new FileSystemEventArgs(WatcherChangeTypes.Created, tempDir, "my_watched_file.txt");
                FileEvent watchedFileEvent = new FileEvent("ScriptFiles", watchedFileEventArgs);

                // Test
                mockEventManager.Publish(ignoredFileEvent);
                await Task.Delay(TimeSpan.FromSeconds(3));
                mockScriptHostManager.Verify(m => m.RestartHostAsync(default), Times.Never);

                mockEventManager.Publish(watchedFileEvent);
                await Task.Delay(TimeSpan.FromSeconds(3));
                mockScriptHostManager.Verify(m => m.RestartHostAsync(default));
            }
        }

        [Theory]
        [InlineData("app_offline.htm", 150, true, false)]
        [InlineData("app_offline.htm", 10, true, false)]
        [InlineData("host.json", 0, false, false)]
        [InlineData("host.json", 200, false, false)]
        [InlineData("host.json", 1000, false, true)]
        public static async Task TestAppOfflineDebounceTime(string fileName, int delayInMs, bool expectShutdown, bool expectRestart)
        {
            using (var directory = new TempDirectory())
            {
                // Setup
                string tempDir = directory.Path;
                Directory.CreateDirectory(Path.Combine(tempDir, "Host"));
                File.Create(Path.Combine(tempDir, fileName));

                var jobHostOptions = new ScriptJobHostOptions
                {
                    RootLogPath = tempDir,
                    RootScriptPath = tempDir,
                    FileWatchingEnabled = true,
                    WatchFiles = { "host.json" }
                };
                var loggerFactory = new LoggerFactory();
                var mockApplicationLifetime = new Mock<IApplicationLifetime>();
                var mockScriptHostManager = new Mock<IScriptHostManager>();
                var mockEventManager = new ScriptEventManager();
                var environment = new TestEnvironment();

                // Act
                FileMonitoringService fileMonitoringService = new FileMonitoringService(new OptionsWrapper<ScriptJobHostOptions>(jobHostOptions),
                    loggerFactory, mockEventManager, mockApplicationLifetime.Object, mockScriptHostManager.Object, environment);
                await fileMonitoringService.StartAsync(new CancellationToken(canceled: false));

                var offlineEventArgs = new FileSystemEventArgs(WatcherChangeTypes.Created, tempDir, fileName);
                FileEvent offlinefileEvent = new FileEvent("ScriptFiles", offlineEventArgs);

                var randomFileEventArgs = new FileSystemEventArgs(WatcherChangeTypes.Created, tempDir, "random.txt");
                FileEvent randomFileEvent = new FileEvent("ScriptFiles", randomFileEventArgs);

                mockEventManager.Publish(offlinefileEvent);
                await Task.Delay(delayInMs);
                mockEventManager.Publish(randomFileEvent);

                // Test
                if (expectShutdown)
                {
                    mockApplicationLifetime.Verify(m => m.StopApplication());
                }
                else
                {
                    mockApplicationLifetime.Verify(m => m.StopApplication(), Times.Never);
                }

                if (expectRestart)
                {
                    mockScriptHostManager.Verify(m => m.RestartHostAsync(default));
                }
                else
                {
                    mockScriptHostManager.Verify(m => m.RestartHostAsync(default), Times.Never);
                }
            }
        }

        public FileMonitoringService GetFileMonitoringService(string tempDir, string rootScriptPath)
        {
            File.Create(Path.Combine(tempDir, "host.json"));

            var jobHostOptions = new ScriptJobHostOptions
            {
                RootLogPath = tempDir,
                RootScriptPath = rootScriptPath,
                FileWatchingEnabled = true
            };
            var loggerFactory = new LoggerFactory();
            var mockApplicationLifetime = new Mock<IApplicationLifetime>();
            var mockScriptHostManager = new Mock<IScriptHostManager>();
            var mockEventManager = new ScriptEventManager();
            var environment = new TestEnvironment();

            // Act
            return new FileMonitoringService(new OptionsWrapper<ScriptJobHostOptions>(jobHostOptions), loggerFactory, mockEventManager, mockApplicationLifetime.Object, mockScriptHostManager.Object, environment);
        }
    }
}
