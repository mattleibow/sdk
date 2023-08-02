﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.DotNet.Cli.NuGetPackageDownloader;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.ToolPackage;
using Microsoft.DotNet.Tools;
using Microsoft.Extensions.EnvironmentAbstractions;
using NuGet.Client;
using NuGet.Common;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Repositories;
using NuGet.RuntimeModel;
using NuGet.Versioning;


namespace Microsoft.DotNet.Cli.ToolPackage
{
    internal class ToolPackageDownloader
    {
        private INuGetPackageDownloader _nugetPackageDownloader;
        private readonly IToolPackageStore _toolPackageStore;

        protected DirectoryPath _toolDownloadDir;
        protected DirectoryPath _toolReturnPackageDirectory;
        protected DirectoryPath _toolReturnJsonParentDirectory;

        protected readonly DirectoryPath _globalToolStageDir;
        protected readonly DirectoryPath _localToolDownloadDir;
        protected readonly DirectoryPath _localToolAssetDir;
        

        public ToolPackageDownloader(
            IToolPackageStore store
        )
        {
            _toolPackageStore = store ?? throw new ArgumentNullException(nameof(store)); ;
            _globalToolStageDir = _toolPackageStore.GetRandomStagingDirectory();
            _localToolDownloadDir = new DirectoryPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "nuget", "package"));
            _localToolAssetDir = new DirectoryPath(PathUtilities.CreateTempSubdirectory());  
        }

        public IToolPackage InstallPackageAsync(PackageLocation packageLocation, PackageId packageId,
            VersionRange versionRange = null,
            string targetFramework = null,
            string verbosity = null,
            bool isGlobalTool = false
            )
        {
            _toolDownloadDir = isGlobalTool ? _globalToolStageDir : _localToolDownloadDir;
            var assetFileDirectory = isGlobalTool ? _globalToolStageDir : _localToolAssetDir;
            _nugetPackageDownloader = new NuGetPackageDownloader.NuGetPackageDownloader(_toolDownloadDir);

            NuGetVersion version = DownloadAndExtractPackage(packageLocation, packageId, _nugetPackageDownloader, _toolDownloadDir.Value).GetAwaiter().GetResult();
            CreateAssetFiles(packageId, version, _toolDownloadDir, assetFileDirectory);

            if (isGlobalTool)
            {
                _toolReturnPackageDirectory = _toolPackageStore.GetPackageDirectory(packageId, version);
                _toolReturnJsonParentDirectory = _toolPackageStore.GetPackageDirectory(packageId, version);
                var packageRootDirectory = _toolPackageStore.GetRootPackageDirectory(packageId);
                Directory.CreateDirectory(packageRootDirectory.Value);
                FileAccessRetrier.RetryOnMoveAccessFailure(() => Directory.Move(_globalToolStageDir.Value, _toolReturnPackageDirectory.Value));
            }
            else
            {
                _toolReturnPackageDirectory = _toolDownloadDir;
                _toolReturnJsonParentDirectory = _localToolAssetDir;
            }

            return new ToolPackageInstance(id: packageId,
                            version: version,
                            packageDirectory: _toolReturnPackageDirectory,
                            assetsJsonParentDirectory: _toolReturnJsonParentDirectory);
        }

        private static void AddToolsAssets(
            ManagedCodeConventions managedCodeConventions,
            LockFileTargetLibrary lockFileLib,
            ContentItemCollection contentItems,
            IReadOnlyList<SelectionCriteria> orderedCriteria)
        {
            var toolsGroup = GetLockFileItems(
                orderedCriteria,
                contentItems,
                managedCodeConventions.Patterns.ToolsAssemblies);

            lockFileLib.ToolsAssemblies.AddRange(toolsGroup);
        }

        private static IEnumerable<LockFileItem> GetLockFileItems(
            IReadOnlyList<SelectionCriteria> criteria,
            ContentItemCollection items,
            params PatternSet[] patterns)
        {
            return GetLockFileItems(criteria, items, additionalAction: null, patterns);
        }

        private static IEnumerable<LockFileItem> GetLockFileItems(
           IReadOnlyList<SelectionCriteria> criteria,
           ContentItemCollection items,
           Action<LockFileItem> additionalAction,
           params PatternSet[] patterns)
        {
            // Loop through each criteria taking the first one that matches one or more items.
            foreach (var managedCriteria in criteria)
            {
                var group = items.FindBestItemGroup(
                    managedCriteria,
                    patterns);

                if (group != null)
                {
                    foreach (var item in group.Items)
                    {
                        var newItem = new LockFileItem(item.Path);
                        object locale;
                        if (item.Properties.TryGetValue("locale", out locale))
                        {
                            newItem.Properties["locale"] = (string)locale;
                        }
                        object related;
                        if (item.Properties.TryGetValue("related", out related))
                        {
                            newItem.Properties["related"] = (string)related;
                        }
                        additionalAction?.Invoke(newItem);
                        yield return newItem;
                    }
                    // Take only the first group that has items
                    break;
                }
            }

            yield break;
        }

        private static async Task<NuGetVersion> DownloadAndExtractPackage(
            PackageLocation packageLocation,
            PackageId packageId,
            INuGetPackageDownloader _nugetPackageDownloader,
            string hashPathLocation
            )
        {
            var packageSourceLocation = new PackageSourceLocation(packageLocation.NugetConfig, packageLocation.RootConfigDirectory, null, packageLocation.AdditionalFeeds);
            var packagePath = await _nugetPackageDownloader.DownloadPackageAsync(packageId, null, packageSourceLocation);

            // look for package on disk and read the version
            NuGetVersion version;

            using (FileStream packageStream = File.OpenRead(packagePath))
            {
                PackageArchiveReader reader = new PackageArchiveReader(packageStream);
                version = new NuspecReader(reader.GetNuspec()).GetVersion();

                var packageHash = Convert.ToBase64String(new CryptoHashProvider("SHA512").CalculateHash(reader.GetNuspec()));
                var hashPath = new VersionFolderPathResolver(hashPathLocation).GetHashPath(packageId.ToString(), version);

                Directory.CreateDirectory(Path.GetDirectoryName(hashPath));
                File.WriteAllText(hashPath, packageHash);
            }

            // Extract the package
            var nupkgDir = Path.Combine(hashPathLocation, packageId.ToString(), version.ToString());
            var filesInPackage = await _nugetPackageDownloader.ExtractPackageAsync(packagePath, new DirectoryPath(nupkgDir));

            if (Directory.Exists(packagePath))
            {
                throw new ToolPackageException(
                    string.Format(
                        CommonLocalizableStrings.ToolPackageConflictPackageId,
                        packageId,
                        version.ToNormalizedString()));
            }
            return version;
        }

        private static void CreateAssetFiles(
            PackageId packageId,
            NuGetVersion version,
            DirectoryPath nugetLocalRepository,
            DirectoryPath assetFileDirectory)
        {
            // To get runtimeGraph:
            var runtimeJsonPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "runtimeIdentifierGraph.json");
            var runtimeGraph = JsonRuntimeFormat.ReadRuntimeGraph(runtimeJsonPath);

            // Create ManagedCodeConventions:
            var conventions = new ManagedCodeConventions(runtimeGraph);

            //  Create LockFileTargetLibrary
            var lockFileLib = new LockFileTargetLibrary()
            {
                Name = packageId.ToString(),
                Version = version,
                Type = LibraryType.Package,
                PackageType = new List<PackageType>() { PackageType.DotnetTool }
            };

            //  Create NuGetv3LocalRepository
            NuGetv3LocalRepository localRepository = new(nugetLocalRepository.Value);
            var package = localRepository.FindPackage(packageId.ToString(), version);

            var collection = new ContentItemCollection();
            collection.Load(package.Files);

            //  Create criteria
            var managedCriteria = new List<SelectionCriteria>(1);
            var currentTargetFramework = NuGetFramework.Parse("net8.0");

            var standardCriteria = conventions.Criteria.ForFrameworkAndRuntime(
                currentTargetFramework,
                RuntimeInformation.RuntimeIdentifier);
            managedCriteria.Add(standardCriteria);

            //  Create asset file
            if (lockFileLib.PackageType.Contains(PackageType.DotnetTool))
            {
                AddToolsAssets(conventions, lockFileLib, collection, managedCriteria);
            }

            var lockFile = new LockFile();
            var lockFileTarget = new LockFileTarget()
            {
                TargetFramework = currentTargetFramework,
                RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier
            };
            lockFileTarget.Libraries.Add(lockFileLib);
            lockFile.Targets.Add(lockFileTarget);
            new LockFileFormat().Write(Path.Combine(assetFileDirectory.Value, "project.assets.json"), lockFile);
        }
    }
}
