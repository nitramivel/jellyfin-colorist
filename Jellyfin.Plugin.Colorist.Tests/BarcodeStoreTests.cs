using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Colorist.Core;
using Jellyfin.Plugin.Colorist.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    /// <summary>
    /// Server paths for a store under test.
    /// </summary>
    /// <remarks>
    /// Everything but <see cref="DataPath"/> throws. <see cref="BarcodeStore"/> is
    /// supposed to need exactly one path out of this interface, and a test double
    /// that quietly returned empty strings for the other fourteen would let it start
    /// depending on them without anybody noticing.
    /// </remarks>
    internal sealed class FakePaths(string dataPath) : IApplicationPaths
    {
        public string DataPath { get; } = dataPath;

        public string ProgramDataPath => throw new NotSupportedException();

        public string WebPath => throw new NotSupportedException();

        public string ProgramSystemPath => throw new NotSupportedException();

        public string ImageCachePath => throw new NotSupportedException();

        public string PluginsPath => throw new NotSupportedException();

        public string PluginConfigurationsPath => throw new NotSupportedException();

        public string LogDirectoryPath => throw new NotSupportedException();

        public string ConfigurationDirectoryPath => throw new NotSupportedException();

        public string SystemConfigurationFilePath => throw new NotSupportedException();

        public string CachePath => throw new NotSupportedException();

        public string TempDirectory => throw new NotSupportedException();

        public string VirtualDataPath => throw new NotSupportedException();

        public string TrickplayPath => throw new NotSupportedException();

        public string BackupPath => throw new NotSupportedException();

        public void MakeSanityCheckOrThrow() => throw new NotSupportedException();

        public void CreateAndCheckMarker(string path, string markerName, bool recursive) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Reading, writing and — the reason this file exists — deleting.
    /// </summary>
    /// <remarks>
    /// The rest of Services is left untested because it needs ffmpeg or a server.
    /// This part needs neither, and it is the only code in the plugin that removes a
    /// file from somebody's library folder. Every test here runs against a real
    /// temporary directory.
    /// </remarks>
    public sealed class BarcodeStoreTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "colorist-tests-" + Guid.NewGuid().ToString("N"));

        private readonly string _library;
        private readonly BarcodeStore _store;

        public BarcodeStoreTests()
        {
            _library = Path.Combine(_root, "library");
            Directory.CreateDirectory(_library);

            _store = new BarcodeStore(
                new FakePaths(Path.Combine(_root, "data")),
                NullLogger<BarcodeStore>.Instance);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked temp directory is not worth failing a test run over.
            }
        }

        private string Media(string name)
        {
            var path = Path.Combine(_library, name);
            File.WriteAllText(path, "not really a video");

            return path;
        }

        private async Task<string> SaveAsync(Guid id, string media, bool withImage)
        {
            var stored = await _store.SaveAsync(
                id,
                media,
                new byte[] { 1, 2, 3 },
                withImage ? new byte[] { 4, 5, 6 } : null,
                besideMedia: true,
                CancellationToken.None);

            return stored.Path;
        }

        [Fact]
        public async Task WritesBothFilesBesideTheMedia()
        {
            var media = Media("Arrival.mkv");
            await SaveAsync(Guid.NewGuid(), media, withImage: true);

            Assert.True(File.Exists(Path.Combine(_library, "Arrival-colorist.json")));
            Assert.True(File.Exists(Path.Combine(_library, "Arrival-colorist.png")));
        }

        [Fact]
        public async Task WritesNoImageWhenImagesAreOff()
        {
            var media = Media("Arrival.mkv");
            await SaveAsync(Guid.NewGuid(), media, withImage: false);

            Assert.True(File.Exists(Path.Combine(_library, "Arrival-colorist.json")));
            Assert.False(File.Exists(Path.Combine(_library, "Arrival-colorist.png")));
        }

        [Fact]
        public async Task RemovesAStaleImageWhenImagesAreTurnedOff()
        {
            // The setting has to apply to items generated before it was flipped,
            // otherwise it only governs films nobody has touched since.
            var media = Media("Arrival.mkv");
            var id = Guid.NewGuid();

            await SaveAsync(id, media, withImage: true);
            Assert.True(File.Exists(Path.Combine(_library, "Arrival-colorist.png")));

            await SaveAsync(id, media, withImage: false);
            Assert.False(File.Exists(Path.Combine(_library, "Arrival-colorist.png")));
            Assert.True(File.Exists(Path.Combine(_library, "Arrival-colorist.json")));
        }

        [Fact]
        public async Task ExistsIgnoresAnImageWithNoColoursBehindIt()
        {
            // A 0.1.0 barcode. The colours cannot be recovered from it, so the item
            // genuinely does need sampling again and must not count as done.
            var media = Media("Arrival.mkv");
            var id = Guid.NewGuid();

            await SaveAsync(id, media, withImage: true);
            File.Delete(Path.Combine(_library, "Arrival-colorist.json"));

            Assert.False(_store.Exists(id, media));
        }

        [Fact]
        public async Task DeleteRemovesBothFiles()
        {
            var media = Media("Arrival.mkv");
            var id = Guid.NewGuid();

            await SaveAsync(id, media, withImage: true);

            Assert.Equal(2, _store.Delete(id, media));
            Assert.Empty(Directory.GetFiles(_library, "*-colorist.*"));
        }

        [Fact]
        public async Task DeleteWithImagesOnlyKeepsTheColours()
        {
            var media = Media("Arrival.mkv");
            var id = Guid.NewGuid();

            await SaveAsync(id, media, withImage: true);

            Assert.Equal(1, _store.Delete(id, media, imagesOnly: true));
            Assert.False(File.Exists(Path.Combine(_library, "Arrival-colorist.png")));
            Assert.True(File.Exists(Path.Combine(_library, "Arrival-colorist.json")));
        }

        [Fact]
        public async Task DeleteTouchesNothingElseInTheFolder()
        {
            // The whole risk of a bulk delete, in one test: these are the names a
            // real movie folder holds, and every one of them must survive.
            var media = Media("Arrival.mkv");
            var bystanders = new[]
            {
                "Arrival-thumb.jpg", "Arrival-poster.png", "Arrival-fanart.png",
                "Arrival.nfo", "Arrival.srt", "folder.jpg", "Arrival-colorist.txt",
            };

            foreach (var name in bystanders)
            {
                File.WriteAllText(Path.Combine(_library, name), "keep me");
            }

            var id = Guid.NewGuid();
            await SaveAsync(id, media, withImage: true);
            _store.Delete(id, media);

            foreach (var name in bystanders)
            {
                Assert.True(File.Exists(Path.Combine(_library, name)), name + " was deleted");
            }

            Assert.True(File.Exists(media), "the video itself was deleted");
        }

        [Fact]
        public void DeleteIsHappyWhenThereIsNothingThere()
        {
            Assert.Equal(0, _store.Delete(Guid.NewGuid(), Path.Combine(_library, "Absent.mkv")));
        }

        [Fact]
        public async Task TheSweepClearsOrphansLeftInPluginData()
        {
            // Items removed from the library never come back through a per-item
            // delete: nothing will ever ask for that ID again.
            var id = Guid.NewGuid();

            await _store.SaveAsync(
                id,
                mediaPath: null,
                new byte[] { 1 },
                new byte[] { 2 },
                besideMedia: false,
                CancellationToken.None);

            var fallback = SidecarPaths.ForFallback(_store.DataRoot, id, SidecarPaths.DataExtension);
            Assert.True(File.Exists(fallback));

            Assert.Equal(2, _store.SweepDataDirectory(imagesOnly: false));
            Assert.False(File.Exists(fallback));
        }

        [Fact]
        public async Task TheSweepCanSpareTheColours()
        {
            var id = Guid.NewGuid();

            await _store.SaveAsync(
                id,
                mediaPath: null,
                new byte[] { 1 },
                new byte[] { 2 },
                besideMedia: false,
                CancellationToken.None);

            Assert.Equal(1, _store.SweepDataDirectory(imagesOnly: true));

            Assert.True(File.Exists(
                SidecarPaths.ForFallback(_store.DataRoot, id, SidecarPaths.DataExtension)));
            Assert.False(File.Exists(
                SidecarPaths.ForFallback(_store.DataRoot, id, SidecarPaths.ImageExtension)));
        }

        [Fact]
        public void TheSweepIsHappyWithNoDataDirectory()
        {
            Assert.Equal(0, _store.SweepDataDirectory(imagesOnly: false));
        }

        [Fact]
        public async Task WritingBesideMediaClearsAnEarlierFallbackCopy()
        {
            // A library folder that was read-only when the barcode was first made and
            // is writable now would otherwise leave two files, with Locate free to
            // return whichever it found first.
            var media = Media("Arrival.mkv");
            var id = Guid.NewGuid();

            await _store.SaveAsync(
                id,
                media,
                new byte[] { 1 },
                null,
                besideMedia: false,
                CancellationToken.None);

            var fallback = SidecarPaths.ForFallback(_store.DataRoot, id, SidecarPaths.DataExtension);
            Assert.True(File.Exists(fallback));

            await SaveAsync(id, media, withImage: false);

            Assert.False(File.Exists(fallback));
            Assert.Equal(
                Path.Combine(_library, "Arrival-colorist.json"),
                _store.Locate(id, media));
        }
    }
}
