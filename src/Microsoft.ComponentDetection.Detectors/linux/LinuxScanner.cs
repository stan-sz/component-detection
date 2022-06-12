using System;
using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ComponentDetection.Common.Telemetry.Records;
using Microsoft.ComponentDetection.Contracts;
using Microsoft.ComponentDetection.Contracts.BcdeModels;
using Microsoft.ComponentDetection.Contracts.TypedComponent;
using Microsoft.ComponentDetection.Detectors.Linux.Contracts;
using MoreLinq.Extensions;
using Newtonsoft.Json;

namespace Microsoft.ComponentDetection.Detectors.Linux
{
    [Export(typeof(ILinuxScanner))]
    public class LinuxScanner : ILinuxScanner
    {
        private const string ScannerImage = "governancecontainerregistry.azurecr.io/syft:0.47.0@sha256:964e0122c7a66a04a855772d0e1f1e2b8d2764baee8e61ca259817b6125129e1";

        private static readonly IList<string> CmdParameters = new List<string>
        {
            "--quiet", "--scope", "all-layers", "--output", "json",
        };

        private static readonly IEnumerable<string> AllowedArtifactTypes = new[] { "apk", "deb", "rpm" };

        private static readonly SemaphoreSlim DockerSemaphore = new SemaphoreSlim(2);

        private static readonly int SemaphoreTimeout = Convert.ToInt32(TimeSpan.FromHours(1).TotalMilliseconds);

        [Import]
        public ILogger Logger { get; set; }

        [Import]
        public IDockerService DockerService { get; set; }

        public async Task<IEnumerable<LayerMappedLinuxComponents>> ScanLinuxAsync(string imageHash, IEnumerable<DockerLayer> dockerLayers, int baseImageLayerCount, CancellationToken cancellationToken = default)
        {
            using var record = new LinuxScannerTelemetryRecord
            {
                ImageToScan = imageHash,
                ScannerVersion = ScannerImage,
            };

            var acquired = false;
            var stdout = string.Empty;
            var stderr = string.Empty;

            using var syftTelemetryRecord = new LinuxScannerSyftTelemetryRecord();

            try
            {
                acquired = await DockerSemaphore.WaitAsync(SemaphoreTimeout, cancellationToken);
                if (acquired)
                {
                    try
                    {
                        var command = new List<string> { imageHash }.Concat(CmdParameters).ToList();
                        (stdout, stderr) = await DockerService.CreateAndRunContainerAsync(ScannerImage, command, cancellationToken);
                    }
                    catch (Exception e)
                    {
                        syftTelemetryRecord.Exception = JsonConvert.SerializeObject(e);
                        Logger.LogException(e, false);
                        throw;
                    }
                }
                else
                {
                    record.SemaphoreFailure = true;
                    Logger.LogWarning($"Failed to enter the docker semaphore for image {imageHash}");
                }
            }
            finally
            {
                if (acquired)
                {
                    DockerSemaphore.Release();
                }
            }

            record.ScanStdErr = stderr;
            record.ScanStdOut = stdout;

            if (string.IsNullOrWhiteSpace(stdout) || !string.IsNullOrWhiteSpace(stderr))
            {
                throw new InvalidOperationException(
                    $"Scan failed with exit info: {stdout}{Environment.NewLine}{stderr}");
            }

            var layerDictionary = dockerLayers
            .DistinctBy(layer => layer.DiffId)
            .ToDictionary(
                layer => layer.DiffId,
                _ => new List<LinuxComponent>());

            try
            {
                var syftOutput = JsonConvert.DeserializeObject<SyftOutput>(stdout);
                var linuxComponentsWithLayers = syftOutput.Artifacts
                    .DistinctBy(artifact => (artifact.Name, artifact.Version))
                    .Where(artifact => AllowedArtifactTypes.Contains(artifact.Type))
                    .Select(artifact =>
                        (Component: new LinuxComponent(syftOutput.Distro.Id, syftOutput.Distro.VersionId, artifact.Name, artifact.Version, ExtractPackageSourceName(artifact)), layerIds: artifact.Locations.Select(location => location.LayerId).Distinct()));

                foreach (var (component, layers) in linuxComponentsWithLayers)
                {
                    layers.ToList().ForEach(layer => layerDictionary[layer].Add(component));
                }

                var layerMappedLinuxComponents = layerDictionary.Select(kvp =>
                {
                    (var layerId, var components) = kvp;
                    return new LayerMappedLinuxComponents
                    {
                        LinuxComponents = components,
                        DockerLayer = dockerLayers.First(layer => layer.DiffId == layerId),
                    };
                });

                syftTelemetryRecord.LinuxComponents = JsonConvert.SerializeObject(linuxComponentsWithLayers.Select(linuxComponentWithLayer =>
                    new
                    {
                        Name = linuxComponentWithLayer.Component.Name,
                        Version = linuxComponentWithLayer.Component.Version,
                    }));

                return layerMappedLinuxComponents;
            }
            catch (Exception e)
            {
                record.FailedDeserializingScannerOutput = e.ToString();
                return null;
            }
        }

        /// <summary>
        /// Extracts a package's upstream source name
        /// For example some distributions package openssl as openssl-dev, libssl, openssl1.0, etc.
        /// </summary>
        private string ExtractPackageSourceName(Package package)
        {
            string name = null;
            switch (package.MetadataType)
            {
                case "ApkMetadata":
                    name = package.Metadata.OriginPackage;
                    break;
                case "DpkgMetadata":
                    if (package.Metadata.Source.HasValue) {
                        name = package.Metadata.Source.Value.String;
                    }

                    break;
                case "RpmdbMetadata":
                    if (!string.IsNullOrWhiteSpace(package.Metadata.SourceRpm)) {
                        // source rpm is name-[epoch:]version-release.src.rpm
                        // where name can contain hyphens but version and release can't,
                        // so extract up to second last hyphen.
                        var parts = package.Metadata.SourceRpm.Split("-");
                        if (parts.Length > 1) {
                            name = string.Join("-", parts[.. (parts.Length - 2)]);
                        }
                    }

                    break;
                default:
                    Logger.LogWarning($"Unknown metadata type: {package.MetadataType}");
                    break;
            }

            return name;
        }
    }
}
