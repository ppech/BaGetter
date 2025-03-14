using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Packaging;

namespace BaGetter.Core;

public class PackageIndexingService : IPackageIndexingService
{
    private readonly IPackageDatabase _packages;
    private readonly IPackageStorageService _storage;
    private readonly ISearchIndexer _search;
    private readonly SystemTime _time;
    private readonly IOptionsSnapshot<BaGetterOptions> _options;
    private readonly IOptionsSnapshot<RetentionOptions> _retentionOptions;
    private readonly ILogger<PackageIndexingService> _logger;
    private readonly IPackageDeletionService _packageDeletionService;

    public PackageIndexingService(
        IPackageDatabase packages,
        IPackageStorageService storage,
        IPackageDeletionService packageDeletionService,
        ISearchIndexer search,
        SystemTime time,
        IOptionsSnapshot<BaGetterOptions> options,
        IOptionsSnapshot<RetentionOptions> retentionOptions,
        ILogger<PackageIndexingService> logger)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retentionOptions = retentionOptions ?? throw new ArgumentNullException(nameof(retentionOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _packageDeletionService = packageDeletionService ?? throw new ArgumentNullException(nameof(packageDeletionService));
#pragma warning disable CS0618 // Type or member is obsolete
        if (_options.Value.MaxVersionsPerPackage > 0)
        {
            _logger.LogError("MaxVersionsPerPackage is deprecated and is not used. Please use MaxMajorVersions, MaxMinorVersions, MaxPatchVersions, and MaxPrereleaseVersions instead.");
        }
#pragma warning restore CS0618 // Type or member is obsolete
    }

    public async Task<PackageIndexingResult> IndexAsync(Stream packageStream, CancellationToken cancellationToken)
    {
        // Try to extract all the necessary information from the package.
        Package package;
        Stream nuspecStream;
        Stream readmeStream;
        Stream iconStream;

        try
        {
            using var packageReader = new PackageArchiveReader(packageStream, leaveStreamOpen: true);
            package = packageReader.GetPackageMetadata();
            package.Published = _time.UtcNow;

            nuspecStream = await packageReader.GetNuspecAsync(cancellationToken);
            nuspecStream = await nuspecStream.AsTemporaryFileStreamAsync(cancellationToken);

            if (package.HasReadme)
            {
                readmeStream = await packageReader.GetReadmeAsync(cancellationToken);
                readmeStream = await readmeStream.AsTemporaryFileStreamAsync(cancellationToken);
            }
            else
            {
                readmeStream = null;
            }

            if (package.HasEmbeddedIcon)
            {
                iconStream = await packageReader.GetIconAsync(cancellationToken);
                iconStream = await iconStream.AsTemporaryFileStreamAsync(cancellationToken);
            }
            else
            {
                iconStream = null;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Uploaded package is invalid");

            return PackageIndexingResult.InvalidPackage;
        }

        // The package is well-formed. Ensure this is a new package.
        if (await _packages.ExistsAsync(package.Id, package.Version, cancellationToken))
        {
            if (_options.Value.AllowPackageOverwrites == PackageOverwriteAllowed.False ||
                (_options.Value.AllowPackageOverwrites == PackageOverwriteAllowed.PrereleaseOnly && !package.IsPrerelease))
            {
                return PackageIndexingResult.PackageAlreadyExists;
            }

            await _packages.HardDeletePackageAsync(package.Id, package.Version, cancellationToken);
            await _storage.DeleteAsync(package.Id, package.Version, cancellationToken);
        }

        // TODO: Add more package validations
        // TODO: Call PackageArchiveReader.ValidatePackageEntriesAsync
        _logger.LogInformation(
            "Validated package {PackageId} {PackageVersion}, persisting content to storage...",
            package.Id,
            package.NormalizedVersionString);

        try
        {
            packageStream.Position = 0;

            await _storage.SavePackageContentAsync(
                package,
                packageStream,
                nuspecStream,
                readmeStream,
                iconStream,
                cancellationToken);
        }
        catch (Exception e)
        {
            // This may happen due to concurrent pushes.
            // TODO: Make IPackageStorageService.SavePackageContentAsync return a result enum so this
            // can be properly handled.
            _logger.LogError(
                e,
                "Failed to persist package {PackageId} {PackageVersion} content to storage",
                package.Id,
                package.NormalizedVersionString);

            throw;
        }

        _logger.LogInformation(
            "Persisted package {Id} {Version} content to storage, saving metadata to database...",
            package.Id,
            package.NormalizedVersionString);

        var result = await _packages.AddAsync(package, cancellationToken);
        if (result == PackageAddResult.PackageAlreadyExists)
        {
            _logger.LogWarning(
                "Package {Id} {Version} metadata already exists in database",
                package.Id,
                package.NormalizedVersionString);

            return PackageIndexingResult.PackageAlreadyExists;
        }

        if (result != PackageAddResult.Success)
        {
            _logger.LogError($"Unknown {nameof(PackageAddResult)} value: {{PackageAddResult}}", result);

            throw new InvalidOperationException($"Unknown {nameof(PackageAddResult)} value: {result}");
        }

        _logger.LogInformation(
            "Successfully persisted package {Id} {Version} metadata to database. Indexing in search...",
            package.Id,
            package.NormalizedVersionString);

        await _search.IndexAsync(package, cancellationToken);

        if (_retentionOptions.Value.MaxMajorVersions.HasValue)
        {
            try { 
                _logger.LogInformation(
                    "Deleting older packages for package {PackageId} {PackageVersion}",
                    package.Id,
                    package.NormalizedVersionString);

                var deleted = await _packageDeletionService.DeleteOldVersionsAsync(
                    package,
                    _retentionOptions.Value.MaxMajorVersions,
                    _retentionOptions.Value.MaxMinorVersions,
                    _retentionOptions.Value.MaxPatchVersions,
                    _retentionOptions.Value.MaxPrereleaseVersions,
                    cancellationToken);
                if (deleted > 0)
                {
                    _logger.LogInformation(
                        "Deleted {packages} older packages for package {PackageId} {PackageVersion}",
                        deleted,
                        package.Id,
                        package.NormalizedVersionString);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(
                    e,
                    "Failed to cleanup older versions of package {PackageId} {PackageVersion}",
                    package.Id,
                    package.NormalizedVersionString);
            }
        }

        _logger.LogInformation(
            "Successfully indexed package {Id} {Version} in search",
            package.Id,
            package.NormalizedVersionString);

        return PackageIndexingResult.Success;
    }
}
