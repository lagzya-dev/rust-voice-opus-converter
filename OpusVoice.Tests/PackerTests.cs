using System;
using System.Collections.Generic;
using System.IO;
using Carbon.Plugins;
using OpusConverter;
using Xunit;

namespace OpusVoice.Tests
{
    public class PackerTests
    {
        private static List<byte[]> FakeFrames(int count)
        {
            var frames = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                var frame = new byte[20 + i];
                for (int b = 0; b < frame.Length; b++)
                {
                    frame[b] = (byte)(i * 31 + b);
                }

                frames.Add(frame);
            }

            return frames;
        }

        [Fact]
        public void Crc32_MatchesTheKnownCheckValue()
        {
            // Standard CRC-32 check: "123456789" -> CBF43926.
            byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, SteamVoicePacker.Crc32(data, data.Length));
        }

        [Fact]
        public void Pack_ProducesTheDocumentedLayoutByte_ForByte()
        {
            var frames = new List<byte[]> { new byte[] { 0xAA, 0xBB, 0xCC }, new byte[] { 0x11 } };
            var buffer = new byte[SteamVoicePacker.MaxPacketSize(2)];

            int length = SteamVoicePacker.Pack(buffer, 0x0110000100355C57UL, 24000, frames, 0, 2, 7);

            byte[] expectedBody =
            {
                0x57, 0x5C, 0x35, 0x00, 0x01, 0x00, 0x10, 0x01, // steam id, little endian
                0x0B, 0xC0, 0x5D,                               // sample rate 24000
                0x06, 0x0C, 0x00,                               // opus chunk, 12 bytes (7 + 5)
                0x03, 0x00, 0x07, 0x00, 0xAA, 0xBB, 0xCC,       // frame 0: len 3, seq 7
                0x01, 0x00, 0x08, 0x00, 0x11,                   // frame 1: len 1, seq 8
            };

            Assert.Equal(expectedBody.Length + 4, length);
            Assert.Equal(expectedBody, buffer[..expectedBody.Length]);
            Assert.Equal(SteamVoiceParser.ReferenceCrc32(expectedBody, expectedBody.Length), BitConverter.ToUInt32(buffer, expectedBody.Length));
        }

        [Fact]
        public void Pack_RoundTripsThroughAnIndependentParser()
        {
            List<byte[]> frames = FakeFrames(10);
            var buffer = new byte[SteamVoicePacker.MaxPacketSize(10)];

            int length = SteamVoicePacker.Pack(buffer, 76561198000000123UL, 48000, frames, 2, 5, 65534);
            ParsedVoice parsed = SteamVoiceParser.Parse(buffer[..length]);

            Assert.Equal(76561198000000123UL, parsed.SteamId);
            Assert.Equal(48000, parsed.SampleRate);
            Assert.Equal(5, parsed.Frames.Count);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(frames[2 + i], parsed.Frames[i].Opus);
            }

            // The 16-bit sequence counter wraps around: 65534, 65535, 0, 1, 2.
            Assert.Equal(new[] { 65534, 65535, 0, 1, 2 }, parsed.Frames.ConvertAll(f => f.Sequence));
        }

        [Fact]
        public void Pack_RejectsBadRanges()
        {
            List<byte[]> frames = FakeFrames(3);
            var buffer = new byte[SteamVoicePacker.MaxPacketSize(3)];

            Assert.Throws<ArgumentOutOfRangeException>(() => SteamVoicePacker.Pack(buffer, 1, 24000, frames, 0, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => SteamVoicePacker.Pack(buffer, 1, 24000, frames, 2, 2, 0));
        }

        [Fact]
        public void Parser_DetectsACorruptedPacket()
        {
            var buffer = new byte[SteamVoicePacker.MaxPacketSize(1)];
            int length = SteamVoicePacker.Pack(buffer, 1, 24000, FakeFrames(1), 0, 1, 0);
            byte[] packet = buffer[..length];
            packet[15] ^= 0xFF;

            Assert.Throws<FormatException>(() => SteamVoiceParser.Parse(packet));
        }

        [Fact]
        public void MaxPacketSize_CoversWorstCaseFrames()
        {
            var big = new List<byte[]>();
            for (int i = 0; i < 10; i++)
            {
                big.Add(new byte[RvoiceFile.MaxOpusPacket]);
            }

            var buffer = new byte[SteamVoicePacker.MaxPacketSize(10)];
            int length = SteamVoicePacker.Pack(buffer, 1, 24000, big, 0, 10, 0);

            Assert.Equal(buffer.Length, length);
        }
    }
}
