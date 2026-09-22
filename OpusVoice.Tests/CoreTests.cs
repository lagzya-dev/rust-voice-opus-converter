using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using OpusConverter;
using Xunit;

namespace OpusVoice.Tests
{
    public class TimeSpecTests
    {
        [Theory]
        [InlineData("90", 90)]
        [InlineData("1.5", 1.5)]
        [InlineData("1,5", 1.5)]
        [InlineData("1:30", 90)]
        [InlineData("00:01:30.5", 90.5)]
        [InlineData("1:02:03", 3723)]
        public void ParsesSecondsAndClockTimes(string text, double expectedSeconds)
        {
            Assert.True(TimeSpec.TryParse(text, out TimeSpan value));
            Assert.Equal(expectedSeconds, value.TotalSeconds, 3);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("-5")]
        [InlineData("1:75")]
        [InlineData("1:2:3:4")]
        public void RejectsNonsense(string text)
        {
            Assert.False(TimeSpec.TryParse(text, out _));
        }

        [Fact]
        public void ParsesTheDurationLineFromFfmpeg()
        {
            TimeSpan? duration = FfmpegProbe.ParseDuration("Input #0, mp3\n  Duration: 00:03:21.45, start: 0.025057, bitrate: 128 kb/s\n");
            Assert.Equal(201.45, duration.Value.TotalSeconds, 2);
            Assert.Null(FfmpegProbe.ParseDuration("  Duration: N/A, bitrate: N/A"));
        }
    }

    public class NamingTests
    {
        [Theory]
        [InlineData(@"C:\music\My Song!.mp3", "My_Song!")]
        [InlineData("https://example.com/a/track%20one.mp3?x=1", "track_one")]
        [InlineData("https://example.com/", "audio")]
        [InlineData(@"C:\music\clip.tar.gz", "clip.tar")]
        public void SuggestsAFileSystemSafeName(string input, string expected)
        {
            Assert.Equal(expected, InputResolver.SuggestName(input));
        }
    }

    public sealed class ProgressAndPreviewTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "opusconv_tests_" + Guid.NewGuid().ToString("N"));

        public ProgressAndPreviewTests()
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

        private sealed class Recorder : IProgress<ConversionProgress>
        {
            public readonly List<ConversionProgress> Reports = new List<ConversionProgress>();

            public void Report(ConversionProgress value)
            {
                lock (Reports)
                {
                    Reports.Add(value);
                }
            }
        }

        private sealed class CancelOnFirstEncodingReport : IProgress<ConversionProgress>
        {
            private readonly CancellationTokenSource _source;

            public CancelOnFirstEncodingReport(CancellationTokenSource source)
            {
                _source = source;
            }

            public void Report(ConversionProgress value)
            {
                if (value.Stage == ConversionStage.Encoding)
                {
                    _source.Cancel();
                }
            }
        }

        [Fact]
        public async Task Progress_GoesThroughAnalyzingAndEncoding_AndEndsAtOne()
        {
            Assert.True(TestAudio.FindFfmpeg() != null, "ffmpeg is required");
            string wav = Path.Combine(_dir, "tone.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(5.0));
            var recorder = new Recorder();

            var converter = new AudioConverter(new ConvertOptions(), _ => { });
            await converter.ConvertAsync(wav, Path.Combine(_dir, "o.rvoice"), CancellationToken.None, recorder);

            Assert.Equal(ConversionStage.Analyzing, recorder.Reports[0].Stage);

            List<ConversionProgress> encoding = recorder.Reports.FindAll(r => r.Stage == ConversionStage.Encoding);
            Assert.True(encoding.Count >= 3, "expected several progress updates for 5 seconds of audio");
            Assert.All(encoding, r => Assert.InRange(r.Fraction.Value, 0.0, 1.0));
            for (int i = 1; i < encoding.Count; i++)
            {
                Assert.True(encoding[i].Fraction >= encoding[i - 1].Fraction, "progress must never go backwards");
            }

            Assert.Equal(1.0, encoding[encoding.Count - 1].Fraction);
        }

        [Fact]
        public async Task Progress_ReflectsStartAndDurationTrimming()
        {
            Assert.True(TestAudio.FindFfmpeg() != null, "ffmpeg is required");
            string wav = Path.Combine(_dir, "long.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(10.0));
            var recorder = new Recorder();

            var options = new ConvertOptions { Start = "2", Duration = "3" };
            await new AudioConverter(options, _ => { }).ConvertAsync(wav, Path.Combine(_dir, "o.rvoice"), CancellationToken.None, recorder);

            // The bar is scaled to the 3 seconds that are converted, not the 10 in the file: it must be near the end just before completion.
            List<ConversionProgress> encoding = recorder.Reports.FindAll(r => r.Stage == ConversionStage.Encoding);
            double beforeLast = encoding[encoding.Count - 2].Fraction.Value;
            Assert.True(beforeLast > 0.7, $"expected the bar to be near the end just before completion, was {beforeLast}");
        }

        [Fact]
        public async Task Download_ReportsProgress_WhenTheServerDeclaresALength()
        {
            Assert.True(TestAudio.FindFfmpeg() != null, "ffmpeg is required");
            byte[] wav = TestAudio.SineWav(20.0);

            var listener = new HttpListener();
            string url = $"http://127.0.0.1:{new Random().Next(20000, 60000)}/";
            listener.Prefixes.Add(url);
            listener.Start();
            Task server = Task.Run(async () =>
            {
                HttpListenerContext context = await listener.GetContextAsync();
                context.Response.ContentType = "audio/wav";
                context.Response.ContentLength64 = wav.Length;
                await context.Response.OutputStream.WriteAsync(wav, 0, wav.Length);
                context.Response.Close();
            });

            try
            {
                var recorder = new Recorder();
                await new AudioConverter(new ConvertOptions(), _ => { }).ConvertAsync(url + "big.wav", Path.Combine(_dir, "o.rvoice"), CancellationToken.None, recorder);
                await server;

                List<ConversionProgress> downloading = recorder.Reports.FindAll(r => r.Stage == ConversionStage.Downloading);
                Assert.NotEmpty(downloading);
                Assert.All(downloading, r => Assert.InRange(r.Fraction.Value, 0.0, 1.0));
            }
            finally
            {
                listener.Close();
            }
        }

        [Fact]
        public async Task Cancelling_StopsTheConversion_AndLeavesNoOutput()
        {
            Assert.True(TestAudio.FindFfmpeg() != null, "ffmpeg is required");
            string wav = Path.Combine(_dir, "long.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(120.0));
            string output = Path.Combine(_dir, "o.rvoice");

            using var cancellation = new CancellationTokenSource();
            var progress = new CancelOnFirstEncodingReport(cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new AudioConverter(new ConvertOptions(), _ => { }).ConvertAsync(wav, output, cancellation.Token, progress));

            Assert.False(File.Exists(output));
        }

        [Fact]
        public async Task PreviewWav_IsAValidWav_ThatStillContainsTheTone()
        {
            Assert.True(TestAudio.FindFfmpeg() != null, "ffmpeg is required");
            string wav = Path.Combine(_dir, "tone.wav");
            File.WriteAllBytes(wav, TestAudio.SineWav(2.0));
            RvoiceFile file = (await new AudioConverter(new ConvertOptions(), _ => { }).ConvertAsync(wav, Path.Combine(_dir, "o.rvoice"), CancellationToken.None)).File;

            byte[] preview = RvoicePreview.ToWav(file);

            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(preview, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(preview, 8, 4));
            Assert.Equal(file.SampleRate, BitConverter.ToInt32(preview, 24));
            int dataBytes = BitConverter.ToInt32(preview, 40);
            Assert.Equal(preview.Length - 44, dataBytes);
            Assert.Equal(file.Frames.Count * file.FrameSamples * 2, dataBytes);

            var pcm = new short[dataBytes / 2];
            Buffer.BlockCopy(preview, 44, pcm, 0, dataBytes);
            double ratio = TestAudio.ToneEnergyRatio(pcm[(file.SampleRate / 10)..], file.SampleRate, TestAudio.ToneHz);
            Assert.True(ratio > 0.9, $"the preview should still be the 440 Hz tone, energy ratio was {ratio:P0}");
        }
    }
}
