using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Colorist.Core;
using Jellyfin.Plugin.Colorist.Core.Color;
using Xunit;

namespace Jellyfin.Plugin.Colorist.Tests
{
    /// <summary>
    /// The stored form of a barcode.
    /// </summary>
    /// <remarks>
    /// These files outlive the version that wrote them — they sit in library
    /// folders people back up and sync — so the round trip and the behaviour on
    /// damaged input are both part of the contract, not incidental.
    /// </remarks>
    public class BarcodeDataTests
    {
        private static IReadOnlyList<Rgb> Sample() => new[]
        {
            new Rgb(0, 0, 0),
            new Rgb(255, 255, 255),
            new Rgb(1, 2, 3),
            new Rgb(171, 205, 239),
            new Rgb(15, 16, 240),
        };

        [Fact]
        public void PacksSixLowercaseHexCharactersPerColour()
        {
            var packed = BarcodeData.Pack(Sample());

            Assert.Equal("000000ffffff010203abcdef0f10f0", packed);
        }

        [Fact]
        public void SurvivesTheRoundTrip()
        {
            var columns = Sample();

            Assert.Equal(columns, BarcodeData.Unpack(BarcodeData.Pack(columns)));
        }

        [Fact]
        public void EveryByteValueSurvivesTheRoundTrip()
        {
            // The nibble table is the kind of code that is right for 0-9 and wrong
            // for a-f, and a barcode is mostly the values in between.
            var columns = Enumerable.Range(0, 256)
                .Select(static v => new Rgb((byte)v, (byte)(255 - v), (byte)((v * 7) % 256)))
                .ToArray();

            Assert.Equal(columns, BarcodeData.Unpack(BarcodeData.Pack(columns)));
        }

        [Fact]
        public void SerializesToTheDocumentTheClientReads()
        {
            using var document = JsonDocument.Parse(BarcodeData.Serialize(Sample()));
            var root = document.RootElement;

            Assert.Equal(BarcodeData.Version, root.GetProperty("v").GetInt32());
            Assert.Equal("000000ffffff010203abcdef0f10f0", root.GetProperty("colors").GetString());
        }

        [Fact]
        public void DeserializeReadsBackWhatSerializeWrote()
        {
            var columns = Sample();

            Assert.Equal(columns, BarcodeData.Deserialize(BarcodeData.Serialize(columns)));
        }

        [Fact]
        public void RefusesToSerializeNothing()
        {
            Assert.Throws<ArgumentException>(() => BarcodeData.Serialize(Array.Empty<Rgb>()));
        }

        [Theory]
        [InlineData("abcde")]      // not a whole number of colours
        [InlineData("abcdefa")]
        [InlineData("gggggg")]     // not hex
        [InlineData("00 000")]
        [InlineData("")]
        [InlineData(null)]
        public void UnpackRejectsAnythingItCannotReadExactly(string? packed)
        {
            Assert.Null(BarcodeData.Unpack(packed));
        }

        [Fact]
        public void UnpackAcceptsUppercase()
        {
            // Not written this way, but a hand edit or another tool might.
            Assert.Equal(
                new[] { new Rgb(0xAB, 0xCD, 0xEF) },
                BarcodeData.Unpack("ABCDEF"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json at all")]
        [InlineData("[]")]
        [InlineData("\"a string\"")]
        [InlineData("{}")]
        [InlineData("{\"colors\":\"000000\"}")]              // no version
        [InlineData("{\"v\":\"1\",\"colors\":\"000000\"}")]  // version is not a number
        [InlineData("{\"v\":1}")]                            // no colours
        [InlineData("{\"v\":1,\"colors\":123}")]
        [InlineData("{\"v\":1,\"colors\":\"zzz\"}")]
        [InlineData(null)]
        public void DeserializeTreatsDamageAsAbsence(string? json)
        {
            // Null rather than an exception, because the caller's response to a
            // damaged file is the same as its response to a missing one: the item
            // has not been generated, so generate it.
            Assert.Null(BarcodeData.Deserialize(json));
        }

        [Fact]
        public void DeserializeRefusesAVersionItDoesNotKnow()
        {
            Assert.Null(BarcodeData.Deserialize("{\"v\":2,\"colors\":\"000000\"}"));
        }

        [Fact]
        public void AThousandColumnsFitsInSixKilobytes()
        {
            // The reason for packing rather than emitting an array of "#rrggbb": this
            // document is fetched on every visit to every detail page.
            var columns = Enumerable.Range(0, 1000)
                .Select(static v => new Rgb((byte)v, (byte)v, (byte)v))
                .ToArray();

            Assert.True(BarcodeData.Serialize(columns).Length < 6200);
        }
    }
}
