using System;
using System.Collections.Generic;

namespace OpusVoice.Tests
{
    public sealed class ParsedVoice
    {
        public ulong SteamId;
        public int SampleRate;
        public List<(int Sequence, byte[] Opus)> Frames = new List<(int, byte[])>();
    }

    /// <summary>
    /// Independent reader for Steam voice payloads, written from the published format description
    /// (Rust crate steam-audio-codec) rather than from the plugin code, so the two can disagree.
    /// </summary>
    public static class SteamVoiceParser
    {
        public static ParsedVoice Parse(byte[] data)
        {
            if (data.Length < 12)
            {
                throw new FormatException("packet too short");
            }

            uint expected = BitConverter.ToUInt32(data, data.Length - 4);
            uint actual = ReferenceCrc32(data, data.Length - 4);
            if (expected != actual)
            {
                throw new FormatException($"CRC mismatch: packet says {expected:X8}, computed {actual:X8}");
            }

            var voice = new ParsedVoice { SteamId = BitConverter.ToUInt64(data, 0) };
            int position = 8;
            int end = data.Length - 4;

            while (position < end)
            {
                byte opcode = data[position++];
                int value = BitConverter.ToUInt16(data, position);
                position += 2;

                switch (opcode)
                {
                    case 0x00: // silence, value = samples
                        break;
                    case 0x0B: // sample rate
                        voice.SampleRate = value;
                        break;
                    case 0x06: // opus plc chunk, value = chunk length
                        int chunkEnd = position + value;
                        if (chunkEnd > end)
                        {
                            throw new FormatException("chunk runs past the packet");
                        }

                        while (chunkEnd - position > 2)
                        {
                            int length = BitConverter.ToUInt16(data, position);
                            position += 2;
                            if (length == 0xFFFF)
                            {
                                continue;
                            }

                            int sequence = BitConverter.ToUInt16(data, position);
                            position += 2;
                            if (position + length > chunkEnd)
                            {
                                throw new FormatException("frame runs past the chunk");
                            }

                            var opus = new byte[length];
                            Array.Copy(data, position, opus, 0, length);
                            position += length;
                            voice.Frames.Add((sequence, opus));
                        }

                        position = chunkEnd;
                        break;
                    default:
                        throw new FormatException("unknown opcode " + opcode);
                }
            }

            return voice;
        }

        // Bitwise CRC-32 (IEEE 802.3), deliberately not the table-driven code the plugin uses.
        public static uint ReferenceCrc32(byte[] data, int length)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (uint)-(int)(crc & 1);
                    crc = (crc >> 1) ^ (0xEDB88320 & mask);
                }
            }

            return ~crc;
        }
    }
}
