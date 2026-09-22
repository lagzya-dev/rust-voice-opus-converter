using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Carbon.Plugins;
using Concentus;
using OpusConverter;
using Xunit;

namespace OpusVoice.Tests
{
    /// <summary>
    /// End-to-end: real audio file or link -> OpusConverter -> .rvoice -> plugin packet builder -> independent parser
    /// -> Opus decoder, and checks that the original 440 Hz tone survives the whole chain.
    /// These need ffmpeg (PATH, FFMPEG_PATH or next to the test binary) and fail loudly if it is missing.
    /// </summary>
    public sealed class ConversionTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "opusconv_tests_" + Guid.NewGuid().ToString("N"));

        public ConversionTests()
        {
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static string RequireFfmpeg()
        {
            string ffmpeg = TestAudio.FindFfmpeg();
            Assert.True(ffmpeg != null, "ffmpeg is required for these tests: install it or set FFMPEG_PATH.");
            return ffmpeg;
        }

        private static AudioConverter NewConverter(ConvertOptions options = null) =>
            new AudioConverter(options ?? new ConvertOptions(), _ => { });

        /// <summary>Sends a converted file through the plugin's packer exactly like the plugin does, then decodes what a client would get.</summary>
        private static short[] PlayThroughPluginPath(RvoiceFile file, int framesPerPacket = 3)
        {
            IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(file.SampleRate, 1);
            var pcm = new List<short>();
            var buffer = new byte[SteamVoicePacker.MaxPacketSize(framesPerPacket)];
            var frames = new List<byte[]>(file.Frames);

            ushort sequence = 0;
            int expectedSequence = 0;
            for (int next = 0; next < frames.Count; next += framesPerPacket)
            {
                int count = Math.Min(framesPerPacket, frames.Count - next);
                int length = SteamVoicePacker.Pack(buffer, 76561198000000001UL, file.SampleRate, frames, next, count, sequence);
                sequence = unchecked((ushort)(sequence + count));

                ParsedVoice parsed = SteamVoiceParser.Parse(buffer[..length]);
                Assert.Equal(file.SampleRate, parsed.SampleRate);
                foreach (var (seq, opus) in parsed.Frames)
                {
                    Assert.Equal(expectedSequence++ & 0xFFFF, seq);
                    var frame = new short[file.FrameSamples];
                    int decoded = decoder.Decode(opus, frame, file.FrameSamples, false);
                    Assert.Equal(file.FrameSamples, decoded);
                    pcm.AddRange(frame);
                }
            }

            return pcm.ToArray();
        }

        private static void AssertToneSurvived(RvoiceFile file, double expectedSeconds)
        {
            Assert.InRange(file.Duration.TotalSeconds, expectedSeconds - 0.15, expectedSeconds + 0.15);

            short[] pcm = PlayThroughPluginPath(file);
            Assert.Equal(file.Frames.Count * file.FrameSamples, pcm.Length);

            // Skip the first 100 ms: the decoder needs a moment to converge.
            int skip = file.SampleRate / 10;
            short[] steady = pcm[skip..];
            double ratio = TestAudio.ToneEnergyRatio(steady, file.SampleRate, TestAudio.ToneHz);
            Assert.True(ratio > 0.9, $"the 440 Hz tone should dominate the decoded audio, but only {ratio:P0} of the energy is at 440 Hz");
        }

        [Fact]
        public async Task Wav_File_ConvertsAndSurvivesTheWholeChain()
        {
            RequireFfmpeg();
            string wav = Path.Combine(_dir, "tone.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(2.0));

            ConversionResult result = await NewConverter().ConvertAsync(wav, Path.Combine(_dir, "tone.rvoice"), CancellationToken.None);

            Assert.True(File.Exists(result.OutputPath));
            RvoiceFile loaded = RvoiceFile.Load(result.OutputPath);
            Assert.Equal(24000, loaded.SampleRate);
            Assert.Equal(480, loaded.FrameSamples);
            AssertToneSurvived(loaded, 2.0);
        }

        [Theory]
        [InlineData("mp3", false)]
        [InlineData("m4a", false)]
        [InlineData("ogg", false)]
        [InlineData("flac", false)]
        [InlineData("mp4", true)]
        [InlineData("mkv", true)]
        public async Task OtherFormats_AndVideoFiles_Convert(string extension, bool withVideo)
        {
            string ffmpeg = RequireFfmpeg();
            string input = Path.Combine(_dir, "in." + extension);
            TestAudio.MakeWithFfmpeg(ffmpeg, input, 2.0, withVideo);

            ConversionResult result = await NewConverter().ConvertAsync(input, Path.Combine(_dir, "out.rvoice"), CancellationToken.None);

            // Lossy containers add encoder padding, so allow a little more slack than for WAV.
            AssertToneSurvived(result.File, 2.0);
        }

        [Theory]
        [InlineData(12000)]
        [InlineData(16000)]
        [InlineData(24000)]
        [InlineData(48000)]
        public async Task EverySupportedSampleRate_Works(int sampleRate)
        {
            RequireFfmpeg();
            string wav = Path.Combine(_dir, "tone.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(1.5));

            var options = new ConvertOptions { SampleRate = sampleRate };
            ConversionResult result = await NewConverter(options).ConvertAsync(wav, Path.Combine(_dir, "r.rvoice"), CancellationToken.None);

            Assert.Equal(sampleRate, result.File.SampleRate);
            Assert.Equal(sampleRate / 50, result.File.FrameSamples);
            AssertToneSurvived(result.File, 1.5);
        }

        [Fact]
        public async Task StartAndDuration_TrimTheAudio()
        {
            RequireFfmpeg();
            string wav = Path.Combine(_dir, "long.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(6.0));

            var options = new ConvertOptions { Start = "1", Duration = "2" };
            ConversionResult result = await NewConverter(options).ConvertAsync(wav, Path.Combine(_dir, "trim.rvoice"), CancellationToken.None);

            AssertToneSurvived(result.File, 2.0);
        }

        [Fact]
        public async Task Volume_ScalesTheLoudness()
        {
            RequireFfmpeg();
            string wav = Path.Combine(_dir, "tone.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(1.5, amplitude: 0.4));

            double Rms(RvoiceFile file)
            {
                short[] pcm = PlayThroughPluginPath(file)[(file.SampleRate / 10)..];
                double sum = 0;
                foreach (short s in pcm)
                {
                    sum += (double)s * s;
                }

                return Math.Sqrt(sum / pcm.Length);
            }

            RvoiceFile normal = (await NewConverter().ConvertAsync(wav, Path.Combine(_dir, "a.rvoice"), CancellationToken.None)).File;
            RvoiceFile quiet = (await NewConverter(new ConvertOptions { Volume = 0.5 }).ConvertAsync(wav, Path.Combine(_dir, "b.rvoice"), CancellationToken.None)).File;

            Assert.InRange(Rms(quiet) / Rms(normal), 0.45, 0.55);
        }

        [Fact]
        public async Task NotAudio_FailsWithAClearError()
        {
            RequireFfmpeg();
            string junk = Path.Combine(_dir, "notes.txt");
            File.WriteAllText(junk, "this is not audio");

            var error = await Assert.ThrowsAsync<NoAudioException>(() =>
                NewConverter().ConvertAsync(junk, Path.Combine(_dir, "x.rvoice"), CancellationToken.None));

            Assert.Contains("ffmpeg produced no audio", error.Message);
            Assert.False(File.Exists(Path.Combine(_dir, "x.rvoice")));
        }

        [Fact]
        public async Task MissingFile_Fails()
        {
            RequireFfmpeg();
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                NewConverter().ConvertAsync(Path.Combine(_dir, "nope.mp3"), Path.Combine(_dir, "x.rvoice"), CancellationToken.None));
        }

        // ---- direct links ----------------------------------------------------------------------------------------------

        private sealed class TestServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly Task _loop;
            public string BaseUrl { get; }
            public string LastUserAgent { get; private set; }

            public TestServer(Func<HttpListenerRequest, HttpListenerResponse, Task> handler)
            {
                int port = new Random().Next(20000, 60000);
                BaseUrl = $"http://127.0.0.1:{port}/";
                _listener.Prefixes.Add(BaseUrl);
                _listener.Start();
                _loop = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext context;
                        try
                        {
                            context = await _listener.GetContextAsync();
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        LastUserAgent = context.Request.UserAgent;
                        try
                        {
                            await handler(context.Request, context.Response);
                        }
                        finally
                        {
                            context.Response.Close();
                        }
                    }
                });
            }

            public void Dispose()
            {
                _listener.Close();
            }
        }

        [Fact]
        public async Task DirectLink_IsDownloadedAndConverted_EvenWithoutAFileExtension()
        {
            RequireFfmpeg();
            byte[] wav = TestAudio.SineWav(2.0);

            using var server = new TestServer(async (request, response) =>
            {
                if (request.Url.AbsolutePath == "/old-location/song.wav")
                {
                    response.StatusCode = 302;
                    response.RedirectLocation = "/media/12345";
                    return;
                }

                response.ContentType = "application/octet-stream";
                response.ContentLength64 = wav.Length;
                await response.OutputStream.WriteAsync(wav, 0, wav.Length);
            });

            ConversionResult result = await NewConverter().ConvertAsync(server.BaseUrl + "old-location/song.wav", Path.Combine(_dir, "web.rvoice"), CancellationToken.None);

            AssertToneSurvived(result.File, 2.0);
            Assert.False(string.IsNullOrEmpty(server.LastUserAgent));
        }

        [Fact]
        public async Task Link_ToAWebPage_IsRejectedWithAHelpfulMessage()
        {
            RequireFfmpeg();
            using var server = new TestServer(async (request, response) =>
            {
                byte[] html = System.Text.Encoding.UTF8.GetBytes("<html>player page</html>");
                response.ContentType = "text/html; charset=utf-8";
                await response.OutputStream.WriteAsync(html, 0, html.Length);
            });

            var error = await Assert.ThrowsAsync<NotDirectLinkException>(() =>
                NewConverter().ConvertAsync(server.BaseUrl + "watch?v=abc", Path.Combine(_dir, "x.rvoice"), CancellationToken.None));

            Assert.Contains("direct link", error.Message);
        }

        [Fact]
        public async Task Link_Returning404_FailsClearly()
        {
            RequireFfmpeg();
            using var server = new TestServer((request, response) =>
            {
                response.StatusCode = 404;
                return Task.CompletedTask;
            });

            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                NewConverter().ConvertAsync(server.BaseUrl + "missing.mp3", Path.Combine(_dir, "x.rvoice"), CancellationToken.None));

            Assert.Contains("404", error.Message);
        }

        [Fact]
        public async Task Link_LargerThanTheLimit_IsRefused()
        {
            RequireFfmpeg();
            var big = new byte[2 * 1024 * 1024];
            using var server = new TestServer(async (request, response) =>
            {
                response.ContentType = "audio/mpeg";
                response.ContentLength64 = big.Length;
                await response.OutputStream.WriteAsync(big, 0, big.Length);
            });

            var options = new ConvertOptions { MaxDownloadMb = 1 };
            var error = await Assert.ThrowsAsync<DownloadTooLargeException>(() =>
                NewConverter(options).ConvertAsync(server.BaseUrl + "huge.mp3", Path.Combine(_dir, "x.rvoice"), CancellationToken.None));

            Assert.Contains("limit", error.Message);
        }

        [Fact]
        public async Task Link_WithNoDeclaredLength_IsStillCappedWhileDownloading()
        {
            RequireFfmpeg();
            using var server = new TestServer(async (request, response) =>
            {
                response.ContentType = "audio/mpeg";
                response.SendChunked = true;
                var chunk = new byte[256 * 1024];
                try
                {
                    for (int i = 0; i < 20; i++)
                    {
                        await response.OutputStream.WriteAsync(chunk, 0, chunk.Length);
                    }
                }
                catch (HttpListenerException)
                {
                }
            });

            var options = new ConvertOptions { MaxDownloadMb = 1 };
            var error = await Assert.ThrowsAsync<DownloadTooLargeException>(() =>
                NewConverter(options).ConvertAsync(server.BaseUrl + "stream.mp3", Path.Combine(_dir, "x.rvoice"), CancellationToken.None));

            Assert.Contains("limit", error.Message);
        }

        [Fact]
        public async Task TemporaryDownload_IsDeletedAfterwards()
        {
            RequireFfmpeg();
            byte[] wav = TestAudio.SineWav(1.0);
            using var server = new TestServer(async (request, response) =>
            {
                response.ContentType = "audio/wav";
                response.ContentLength64 = wav.Length;
                await response.OutputStream.WriteAsync(wav, 0, wav.Length);
            });

            int Count() => Directory.GetFiles(Path.GetTempPath(), "opusconv_*.bin").Length;
            int before = Count();

            await NewConverter().ConvertAsync(server.BaseUrl + "a.wav", Path.Combine(_dir, "a.rvoice"), CancellationToken.None);

            Assert.Equal(before, Count());
        }
    }

    public class RvoiceFileTests
    {
        [Fact]
        public void WriteThenRead_RoundTrips()
        {
            var frames = new List<byte[]> { new byte[] { 1, 2, 3 }, new byte[] { 4 }, new byte[1275] };
            var file = new RvoiceFile(24000, 480, frames);

            using var stream = new MemoryStream();
            file.Write(stream);
            stream.Position = 0;
            RvoiceFile loaded = RvoiceFile.Read(stream);

            Assert.Equal(24000, loaded.SampleRate);
            Assert.Equal(480, loaded.FrameSamples);
            Assert.Equal(frames, loaded.Frames);
        }

        [Fact]
        public void Header_MatchesWhatThePluginExpects()
        {
            using var stream = new MemoryStream();
            new RvoiceFile(24000, 480, new List<byte[]> { new byte[] { 9, 8 } }).Write(stream);
            byte[] bytes = stream.ToArray();

            byte[] expected =
            {
                (byte)'R', (byte)'V', (byte)'O', (byte)'C',
                0x01, 0x00,                   // version 1
                0xC0, 0x5D, 0x00, 0x00,       // 24000 Hz
                0xE0, 0x01,                   // 480 samples per frame
                0x01, 0x00, 0x00, 0x00,       // one frame
                0x02, 0x00, 9, 8,             // frame: length 2 + data
            };

            Assert.Equal(expected, bytes);
        }

        [Fact]
        public void Read_RejectsGarbageAndTruncatedFiles()
        {
            Assert.Throws<InvalidDataException>(() => RvoiceFile.Read(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })));

            using var stream = new MemoryStream();
            new RvoiceFile(24000, 480, new List<byte[]> { new byte[50] }).Write(stream);
            byte[] truncated = stream.ToArray()[..30];
            Assert.ThrowsAny<Exception>(() => RvoiceFile.Read(new MemoryStream(truncated)));
        }

        [Fact]
        public void Write_RejectsEmptyOrOversizedPackets()
        {
            Assert.Throws<InvalidDataException>(() => new RvoiceFile(24000, 480, new List<byte[]> { new byte[0] }).Write(new MemoryStream()));
            Assert.Throws<InvalidDataException>(() => new RvoiceFile(24000, 480, new List<byte[]> { new byte[1276] }).Write(new MemoryStream()));
        }
    }

    public class ArgumentParserTests
    {
        [Fact]
        public void ParsesInputsAndOptions()
        {
            CliArguments cli = ArgumentParser.Parse(new[] { "a.mp3", "https://x/y.mp3", "-o", "out", "-b", "64", "-v", "1,5", "-n", "-m", "voice", "-s", "00:00:05", "-t", "20" });

            Assert.Equal(new[] { "a.mp3", "https://x/y.mp3" }, cli.Inputs);
            Assert.Equal("out", cli.Output);
            Assert.Equal(64, cli.Options.BitrateKbps);
            Assert.Equal(24000, cli.Options.SampleRate); // fixed - Rust's voice chat rate, not user-configurable
            Assert.Equal(1.5, cli.Options.Volume);
            Assert.True(cli.Options.Normalize);
            Assert.Equal(EncodeMode.Voice, cli.Options.Mode);
            Assert.Equal("00:00:05", cli.Options.Start);
            Assert.Equal("20", cli.Options.Duration);
        }

        [Theory]
        [InlineData("-b", "1")]
        [InlineData("-b", "abc")]
        [InlineData("-v", "0")]
        [InlineData("-m", "loud")]
        public void RejectsBadValues(string option, string value)
        {
            Assert.Throws<ArgumentException>(() => ArgumentParser.Parse(new[] { "a.mp3", option, value }));
        }

        [Fact]
        public void RejectsUnknownOptionsAndMissingValues()
        {
            Assert.Throws<ArgumentException>(() => ArgumentParser.Parse(new[] { "a.mp3", "--bogus" }));
            Assert.Throws<ArgumentException>(() => ArgumentParser.Parse(new[] { "a.mp3", "-o" }));
        }
    }
}
