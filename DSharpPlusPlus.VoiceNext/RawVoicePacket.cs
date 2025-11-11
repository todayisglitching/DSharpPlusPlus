using System;

namespace DSharpPlusPlus.VoiceNext;

internal readonly struct RawVoicePacket
{
    public RawVoicePacket(Memory<byte> bytes, int duration, bool silence)
    {
        this.bytes = bytes;
        this.duration = duration;
        this.silence = silence;
        this.rentedBuffer = null;
    }

    public RawVoicePacket(Memory<byte> bytes, int duration, bool silence, byte[] rentedBuffer)
        : this(bytes, duration, silence) => this.rentedBuffer = rentedBuffer;

    public readonly Memory<byte> bytes;
    public readonly int duration;
    public readonly bool silence;

    public readonly byte[] rentedBuffer;
}
