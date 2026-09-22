using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;

namespace OpusVoice.Tests
{
    public static class TestAudio
    {
        public const double ToneHz = 440.0;

        /// <summary>Writes a mono 16-bit PCM WAV containing a sine tone.</summary>
        public static byte[] SineWav(double seconds, int sampleRate = 44100, double hz = ToneHz, double amplitude = 0.5)
        {
            int samples = (int)(seconds * sampleRate);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + samples * 2);
                writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(samples * 2);
                for (int i = 0; i < samples; i++)
                {
                    writer.Write((short)(short.MaxValue * amplitude * Math.Sin(2 * Math.PI * hz * i / sampleRate)));
                }

                return stream.ToArray();
            }
        }

        /// <summary>Fraction of the signal's energy that sits at <paramref name="hz"/> (1.0 = a pure tone).</summary>
        public static double ToneEnergyRatio(short[] pcm, int sampleRate, double hz)
        {
            Complex sum = Complex.Zero;
            double energy = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                double sample = pcm[i];
                sum += sample * Complex.FromPolarCoordinates(1, -2 * Math.PI * hz * i / sampleRate);
                energy += sample * sample;
            }

            if (energy == 0)
            {
                return 0;
            }

            // |DFT bin|^2 * 2 / N equals the tone's energy for a pure sinusoid.
            double toneEnergy = 2 * sum.Magnitude * sum.Magnitude / pcm.Length;
            return toneEnergy / energy;
        }

        public static string FindFfmpeg()
        {
            try
            {
                return OpusConverter.Ffmpeg.Locate(null);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Uses ffmpeg to synthesize a test file in the container implied by the extension.</summary>
        public static void MakeWithFfmpeg(string ffmpeg, string outputPath, double seconds, bool withVideo = false)
        {
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (string a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency={ToneHz}:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:sample_rate=44100" })
            {
                info.ArgumentList.Add(a);
            }

            if (withVideo)
            {
                foreach (string a in new[] { "-f", "lavfi", "-i", $"testsrc=size=160x120:rate=10:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-c:v", "mpeg4", "-shortest" })
                {
                    info.ArgumentList.Add(a);
                }
            }

            info.ArgumentList.Add(outputPath);

            using (Process process = Process.Start(info))
            {
                string errors = process.StandardError.ReadToEnd();
                process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"ffmpeg could not create {Path.GetFileName(outputPath)}: {errors}");
                }
            }
        }
    }
}
