using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.AiTranslate.Dubbing;

/// <summary>Minimal 16-bit PCM WAV reading/writing.</summary>
public static class Wav
{
    /// <summary>Reads a 16-bit PCM WAV; stereo is mixed down to mono.</summary>
    public static (short[] Samples, int SampleRate) ReadPcm16Mono(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Not a WAV file");
        }

        int channels = 1, rate = 24000, bits = 16;
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4));
            int body = pos + 8;
            if (id == "fmt ")
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 14));
            }
            else if (id == "data")
            {
                if (bits != 16)
                {
                    throw new InvalidDataException("Only 16-bit PCM is supported");
                }

                // Some encoders write 0 or 0xFFFFFFFF for streamed data sizes.
                int length = size <= 0 || body + size > wav.Length ? wav.Length - body : size;
                int frames = length / (2 * channels);
                var samples = new short[frames];
                for (int f = 0; f < frames; f++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + (((f * channels) + c) * 2)));
                    }

                    samples[f] = (short)(sum / channels);
                }

                return (samples, rate);
            }

            pos = body + size + (size & 1);
        }

        throw new InvalidDataException("WAV has no data chunk");
    }

    public static byte[] WritePcm16Mono(short[] samples, int sampleRate)
    {
        var bytes = new byte[44 + (samples.Length * 2)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(44 + (i * 2)), samples[i]);
        }

        return bytes;
    }
}
