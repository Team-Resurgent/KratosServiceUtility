using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EspDotNet.Communication
{
    /*  In slipframing, the following rules apply:
     *  0xC0 => 0xDB 0xDC
     *  0xDC => 0xDB 0xDD
     */

    public class SlipFraming
    {
        private const byte FrameDelimiter = 0xC0;
        private const byte EscapeByte = 0xDB;
        private const byte EscapeFrameDelimiter = 0xDC; 
        private const byte EscapeEscapeByte = 0xDD;
        private readonly SerialPort _serialPort;

        public SlipFraming(SerialPort serialPort)
        {
            _serialPort = serialPort;
        }

        public async Task WriteFrameAsync(Frame frame, CancellationToken token)
        {
            byte[] escapedFrame = EscapeFrame(frame);
            
            // Use WriteAsync instead of WriteByte to avoid potential blocking
            byte[] sof = new byte[] { FrameDelimiter };
            await _serialPort.BaseStream.WriteAsync(sof, 0, 1, token);
            
            await _serialPort.BaseStream.WriteAsync(escapedFrame, 0, escapedFrame.Length, token);
            
            byte[] eof = new byte[] { FrameDelimiter };
            await _serialPort.BaseStream.WriteAsync(eof, 0, 1, token);
            
            await _serialPort.BaseStream.FlushAsync(token);
        }

        public async Task<Frame?> ReadFrameAsync(CancellationToken token)
        {
            List<byte> escapedFrameBuffer = new List<byte>();

            // In slipframing, all delimiters are replaced, so we can record everything between delimeters and decode it later
            while (true)
            {
                byte currentByte = await ReadByte(token);

                if (currentByte == FrameDelimiter)
                {
                    // If we havent recieved any data yet, this is the SOF
                    if (escapedFrameBuffer.Count > 0)
                        return Unescape(escapedFrameBuffer.ToArray());
                }
                else
                {
                    escapedFrameBuffer.Add(currentByte);
                }
            }
        }

        private async Task<byte> ReadByte(CancellationToken token)
        {
            // Wait for data with timeout to prevent infinite hangs
            // Use longer timeout for operations like erase that can take 30+ seconds
            const int maxWaitMs = 60000; // 60 seconds max wait per byte (erase operations need this)
            int elapsedMs = 0;
            const int pollIntervalMs = 10;
            
            while (_serialPort.BytesToRead == 0)
            {
                token.ThrowIfCancellationRequested();
                
                if (elapsedMs >= maxWaitMs)
                    throw new TimeoutException("Timeout waiting for serial data");
                    
                await Task.Delay(pollIntervalMs, token);
                elapsedMs += pollIntervalMs;
            }

            return (byte)_serialPort.ReadByte();
        }

        private byte[] EscapeFrame(Frame frame)
        {
            List<byte> buffer = new();

            foreach (byte b in frame.Data)
            {
                if (b == FrameDelimiter)
                {
                    buffer.Add(EscapeByte);
                    buffer.Add(EscapeFrameDelimiter);
                }
                else if (b == EscapeByte)
                {
                    buffer.Add(EscapeByte);
                    buffer.Add(EscapeEscapeByte);
                }
                else
                {
                    buffer.Add(b);
                }
            }
            return buffer.ToArray();
        }

        private Frame? Unescape(byte[] data)
        {
            List<byte> buffer = new List<byte>();

            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == EscapeByte)
                {
                    i++;
                    if (i >= data.Length) break;

                    if (data[i] == EscapeFrameDelimiter)
                    {
                        buffer.Add(FrameDelimiter);
                    }
                    else if (data[i] == EscapeEscapeByte)
                    {
                        buffer.Add(EscapeByte);
                    }
                }
                else
                {
                    buffer.Add(data[i]);
                }
            }

            return new Frame(buffer.ToArray());
        }
    }
}
