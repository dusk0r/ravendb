﻿using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Sparrow.Extensions;

namespace Sparrow.Json
{
    public unsafe class BlittableJsonTextWriter : IDisposable
    {
        private readonly JsonOperationContext _context;
        private readonly Stream _stream;
        private const byte StartObject = (byte)'{';
        private const byte EndObject = (byte)'}';
        private const byte StartArray = (byte)'[';
        private const byte EndArray = (byte)']';
        private const byte Comma = (byte)',';
        private const byte Quote = (byte)'"';
        private const byte Colon = (byte)':';
        public static readonly byte[] NaNBuffer = { (byte)'"', (byte)'N', (byte)'a', (byte)'N', (byte)'"' };
        public static readonly byte[] PositiveInfinityBuffer =
        {
            (byte)'"', (byte)'I', (byte)'n', (byte)'f', (byte)'i', (byte)'n', (byte)'i', (byte)'t', (byte)'y', (byte)'"'
        };
        public static readonly byte[] NegativeInfinityBuffer =
        {
            (byte)'"', (byte)'-', (byte)'I', (byte)'n', (byte)'f', (byte)'i', (byte)'n', (byte)'i', (byte)'t', (byte)'y', (byte)'"'
        };
        public static readonly byte[] NullBuffer = { (byte)'n', (byte)'u', (byte)'l', (byte)'l', };
        public static readonly byte[] TrueBuffer = { (byte)'t', (byte)'r', (byte)'u', (byte)'e', };
        public static readonly byte[] FalseBuffer = { (byte)'f', (byte)'a', (byte)'l', (byte)'s', (byte)'e', };

        private static readonly byte[] EscapeCharacters;

        private int _pos;
        private readonly byte* _buffer;
        private JsonOperationContext.ReturnBuffer _returnBuffer;
        private readonly JsonOperationContext.ManagedPinnedBuffer _pinnedBuffer;
        private readonly AllocatedMemoryData _parserAuxiliarMemory;

        static BlittableJsonTextWriter()
        {
            EscapeCharacters = new byte[256];
            for (int i = 0; i < EscapeCharacters.Length; i++)
                EscapeCharacters[i] = 255;

            EscapeCharacters[(byte)'\b'] = (byte)'b';
            EscapeCharacters[(byte)'\t'] = (byte)'t';
            EscapeCharacters[(byte)'\n'] = (byte)'n';
            EscapeCharacters[(byte)'\f'] = (byte)'f';
            EscapeCharacters[(byte)'\r'] = (byte)'r';
            EscapeCharacters[(byte)'\\'] = (byte)'\\';
            EscapeCharacters[(byte)'/'] = (byte)'/';
            EscapeCharacters[(byte)'"'] = (byte)'"';
        }

        public BlittableJsonTextWriter(JsonOperationContext context, Stream stream)
        {
            _context = context;
            _stream = stream;

            _returnBuffer = context.GetManagedBuffer(out _pinnedBuffer);
            _buffer = _pinnedBuffer.Pointer;

            _parserAuxiliarMemory = context.GetMemory(32);
        }

        public int Position => _pos;

        public override string ToString()
        {
            return Encodings.Utf8.GetString(_pinnedBuffer.Buffer.Array, _pinnedBuffer.Buffer.Offset, _pos);
        }

        public void WriteObjectOrdered(BlittableJsonReaderObject obj)
        {
            WriteStartObject();
            var props = obj.GetPropertiesByInsertionOrder();
            var prop = new BlittableJsonReaderObject.PropertyDetails();
            for (int i = 0; i < props.Length; i++)
            {
                if (i != 0)
                {
                    WriteComma();
                }

                obj.GetPropertyByIndex(props[i], ref prop);
                WritePropertyName(prop.Name);

                WriteValue(prop.Token & BlittableJsonReaderBase.TypesMask, prop.Value, originalPropertyOrder: true);
            }

            WriteEndObject();
        }

        public void WriteObject(BlittableJsonReaderObject obj)
        {
            if (obj == null)
            {
                WriteNull();
                return;
            }
            WriteStartObject();
            var prop = new BlittableJsonReaderObject.PropertyDetails();
            for (int i = 0; i < obj.Count; i++)
            {
                if (i != 0)
                {
                    WriteComma();
                }
                obj.GetPropertyByIndex(i, ref prop);
                WritePropertyName(prop.Name);

                WriteValue(prop.Token & BlittableJsonReaderBase.TypesMask, prop.Value, originalPropertyOrder: false);
            }

            WriteEndObject();
        }


        private void WriteArrayToStream(BlittableJsonReaderArray blittableArray, bool originalPropertyOrder)
        {
            WriteStartArray();
            var length = blittableArray.Length;
            for (var i = 0; i < length; i++)
            {
                var propertyValueAndType = blittableArray.GetValueTokenTupleByIndex(i);

                if (i != 0)
                {
                    WriteComma();
                }
                // write field value
                WriteValue(propertyValueAndType.Item2, propertyValueAndType.Item1, originalPropertyOrder);

            }
            WriteEndArray();
        }

        public void WriteValue(BlittableJsonToken token, object val, bool originalPropertyOrder = false)
        {
            switch (token)
            {
                case BlittableJsonToken.String:
                    WriteString((LazyStringValue)val);
                    break;
                case BlittableJsonToken.Integer:
                    WriteInteger((long)val);
                    break;
                case BlittableJsonToken.StartArray:
                    WriteArrayToStream((BlittableJsonReaderArray)val, originalPropertyOrder);
                    break;
                case BlittableJsonToken.EmbeddedBlittable:
                case BlittableJsonToken.StartObject:
                    var blittableJsonReaderObject = ((BlittableJsonReaderObject)val);
                    if (originalPropertyOrder)
                        WriteObjectOrdered(blittableJsonReaderObject);
                    else
                        WriteObject(blittableJsonReaderObject);
                    break;
                case BlittableJsonToken.CompressedString:
                    WriteString((LazyCompressedStringValue)val);
                    break;
                case BlittableJsonToken.LazyNumber:
                    WriteDouble((LazyNumberValue)val);
                    break;
                case BlittableJsonToken.Boolean:
                    WriteBool((bool)val);
                    break;
                case BlittableJsonToken.Null:
                    WriteNull();
                    break;
                default:
                    throw new DataMisalignedException($"Unidentified Type {token}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDateTime(DateTime value, bool isUtc)
        {
            int size = value.GetDefaultRavenFormat(_parserAuxiliarMemory, isUtc);

            var strBuffer = _parserAuxiliarMemory.Address;

            WriteRawStringWhichMustBeWithoutEscapeChars(strBuffer, size);
        }

        public void WriteString(string str)
        {
            using (var lazyStr = _context.GetLazyString(str))
            {
                WriteString(lazyStr);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteString(LazyStringValue str)
        {
            if (str == null)
            {
                WriteNull();
                return;
            }

            var strBuffer = str.Buffer;
            var size = str.Size;

            var escapeSequencePos = size;
            var numberOfEscapeSequences = BlittableJsonReaderBase.ReadVariableSizeInt(str.Buffer, ref escapeSequencePos);

            // We ensure our buffer will have enough space to deal with the whole string.
            int bufferSize = 2 * numberOfEscapeSequences + size + 1;
            if (bufferSize >= JsonOperationContext.ManagedPinnedBuffer.Size)
            {
                UnlikelyWriteLargeString(strBuffer, size, numberOfEscapeSequences, escapeSequencePos); // OK, do it the slow way. 
                return;
            }

            EnsureBuffer(size + 2);
            _buffer[_pos++] = Quote;

            if (numberOfEscapeSequences == 0)
            {
                // PERF: Fast Path. 
                WriteRawString(strBuffer, size);
            }
            else
            {
                UnlikelyWriteEscapeSequences(strBuffer, size, numberOfEscapeSequences, escapeSequencePos);
            }

            _buffer[_pos++] = Quote;
        }

        private void UnlikelyWriteEscapeSequences(byte* strBuffer, int size, int numberOfEscapeSequences, int escapeSequencePos)
        {
            // We ensure our buffer will have enough space to deal with the whole string.
            int bufferSize = 2 * numberOfEscapeSequences + size + 1;

            EnsureBuffer(bufferSize);

            var ptr = strBuffer;
            var buffer = _buffer;
            while (numberOfEscapeSequences > 0)
            {
                numberOfEscapeSequences--;
                var bytesToSkip = BlittableJsonReaderBase.ReadVariableSizeInt(ptr, ref escapeSequencePos);
                WriteRawString(strBuffer, bytesToSkip);
                strBuffer += bytesToSkip;
                size -= bytesToSkip + 1 /*for the escaped char we skip*/;
                var b = *(strBuffer++);

                int auxPos = _pos;
                buffer[auxPos++] = (byte)'\\';
                buffer[auxPos++] = GetEscapeCharacter(b);
                _pos = auxPos;
            }

            // write remaining (or full string) to the buffer in one shot
            WriteRawString(strBuffer, size);
        }

        private void UnlikelyWriteLargeString(byte* strBuffer, int size, int numberOfEscapeSequences, int escapeSequencePos)
        {
            var ptr = strBuffer;

            EnsureBuffer(1);
            _buffer[_pos++] = Quote;

            while (numberOfEscapeSequences > 0)
            {
                numberOfEscapeSequences--;
                var bytesToSkip = BlittableJsonReaderBase.ReadVariableSizeInt(ptr, ref escapeSequencePos);

                EnsureBuffer(bytesToSkip);
                UnlikelyWriteLargeRawString(strBuffer, bytesToSkip);
                strBuffer += bytesToSkip;
                size -= bytesToSkip + 1 /*for the escaped char we skip*/;
                var b = *(strBuffer++);

                EnsureBuffer(2);
                _buffer[_pos++] = (byte)'\\';
                _buffer[_pos++] = GetEscapeCharacter(b);
            }

            // write remaining (or full string) to the buffer in one shot
            UnlikelyWriteLargeRawString(strBuffer, size);

            EnsureBuffer(1);
            _buffer[_pos++] = Quote;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte GetEscapeCharacter(byte b)
        {
            byte r = EscapeCharacters[b];
            if (r == 255)
                ThrowInvalidEscapeCharacter(b);
            return r;
        }

        private void ThrowInvalidEscapeCharacter(byte b)
        {
            throw new InvalidOperationException("Invalid escape char '" + (char)b + "' numeric value is: " + b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteString(LazyCompressedStringValue str)
        {
            var strBuffer = str.DecompressToTempBuffer(out AllocatedMemoryData allocated, _context);

            try
            {
                var strSrcBuffer = str.Buffer;

                var size = str.UncompressedSize;
                var escapeSequencePos = str.CompressedSize;
                var numberOfEscapeSequences = BlittableJsonReaderBase.ReadVariableSizeInt(strSrcBuffer, ref escapeSequencePos);

                // We ensure our buffer will have enough space to deal with the whole string.
                int bufferSize = 2 * numberOfEscapeSequences + size + 2;
                if (bufferSize >= JsonOperationContext.ManagedPinnedBuffer.Size)
                    goto WriteLargeCompressedString; // OK, do it the slow way instead.

                EnsureBuffer(bufferSize);

                _buffer[_pos++] = Quote;
                while (numberOfEscapeSequences > 0)
                {
                    numberOfEscapeSequences--;
                    var bytesToSkip = BlittableJsonReaderBase.ReadVariableSizeInt(strSrcBuffer, ref escapeSequencePos);
                    WriteRawString(strBuffer, bytesToSkip);
                    strBuffer += bytesToSkip;
                    size -= bytesToSkip + 1 /*for the escaped char we skip*/;
                    var b = *(strBuffer++);

                    var auxPos = _pos;
                    _buffer[auxPos++] = (byte)'\\';
                    _buffer[auxPos++] = GetEscapeCharacter(b);
                    _pos = auxPos;
                }

                // write remaining (or full string) to the buffer in one shot
                WriteRawString(strBuffer, size);

                _buffer[_pos++] = Quote;

                return;

WriteLargeCompressedString:
                UnlikelyWriteCompressedString(numberOfEscapeSequences, strSrcBuffer, escapeSequencePos, strBuffer, size);
            }
            finally
            {
                if (allocated != null) //precaution
                    _context.ReturnMemory(allocated);
            }
        }

        private void UnlikelyWriteCompressedString(int numberOfEscapeSequences, byte* strSrcBuffer, int escapeSequencePos, byte* strBuffer, int size)
        {
            EnsureBuffer(1);
            _buffer[_pos++] = Quote;
            while (numberOfEscapeSequences > 0)
            {
                numberOfEscapeSequences--;
                var bytesToSkip = BlittableJsonReaderBase.ReadVariableSizeInt(strSrcBuffer, ref escapeSequencePos);
                EnsureBuffer(bytesToSkip);
                WriteRawString(strBuffer, bytesToSkip);
                strBuffer += bytesToSkip;
                size -= bytesToSkip + 1 /*for the escaped char we skip*/;
                var b = *(strBuffer++);

                EnsureBuffer(2);
                _buffer[_pos++] = (byte)'\\';
                _buffer[_pos++] = GetEscapeCharacter(b);
            }

            // write remaining (or full string) to the buffer in one shot
            WriteRawString(strBuffer, size);

            EnsureBuffer(1);
            _buffer[_pos++] = Quote;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRawStringWhichMustBeWithoutEscapeChars(byte* buffer, int size)
        {
            EnsureBuffer(size + 2);
            _buffer[_pos++] = Quote;
            WriteRawString(buffer, size);
            _buffer[_pos++] = Quote;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteRawString(byte* buffer, int size)
        {
            // PERF: We are no longer ensuring the buffer has enough size anymore. Caller must ensure it is so.
            if (size < JsonOperationContext.ManagedPinnedBuffer.Size)
            {
                EnsureBuffer(size);
                Memory.Copy(_buffer + _pos, buffer, size);
                _pos += size;
                return;
            }

            UnlikelyWriteLargeRawString(buffer, size);
        }

        private void UnlikelyWriteLargeRawString(byte* buffer, int size)
        {
            // need to do this in pieces
            var posInStr = 0;
            while (posInStr < size)
            {
                var amountToCopy = Math.Min(size - posInStr, JsonOperationContext.ManagedPinnedBuffer.Size);
                Flush();
                Memory.Copy(_buffer, buffer + posInStr, amountToCopy);
                posInStr += amountToCopy;
                _pos = amountToCopy;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteStartObject()
        {
            EnsureBuffer(1);
            _buffer[_pos++] = StartObject;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteEndArray()
        {
            EnsureBuffer(1);
            _buffer[_pos++] = EndArray;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteStartArray()
        {
            EnsureBuffer(1);
            _buffer[_pos++] = StartArray;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteEndObject()
        {
            EnsureBuffer(1);
            _buffer[_pos++] = EndObject;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureBuffer(int len)
        {
            if (len >= JsonOperationContext.ManagedPinnedBuffer.Size)
                ThrowValueTooBigForBuffer();
            if (_pos + len < JsonOperationContext.ManagedPinnedBuffer.Size)
                return;

            Flush();
        }

        public void Flush()
        {
            if (_stream == null)
                ThrowStreamClosed();
            if (_pos == 0)
                return;
            _stream.Write(_pinnedBuffer.Buffer.Array, _pinnedBuffer.Buffer.Offset, _pos);
            _pos = 0;
        }

        private static void ThrowValueTooBigForBuffer()
        {
            // ReSharper disable once NotResolvedInText
            throw new ArgumentOutOfRangeException("len");
        }

        private void ThrowStreamClosed()
        {
            throw new ObjectDisposedException("The stream was closed already.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteNull()
        {
            EnsureBuffer(4);
            for (int i = 0; i < 4; i++)
            {
                _buffer[_pos++] = NullBuffer[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(bool val)
        {
            EnsureBuffer(5);
            var buffer = val ? TrueBuffer : FalseBuffer;
            for (int i = 0; i < buffer.Length; i++)
            {
                _buffer[_pos++] = buffer[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteComma()
        {
            EnsureBuffer(1);
            _buffer[_pos++] = Comma;

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WritePropertyName(LazyStringValue prop)
        {
            WriteString(prop);
            EnsureBuffer(1);
            _buffer[_pos++] = Colon;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WritePropertyName(string prop)
        {
            var lazyProp = _context.GetLazyStringForFieldWithCaching(prop);
            WriteString(lazyProp);
            EnsureBuffer(1);
            _buffer[_pos++] = Colon;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WritePropertyName(StringSegment prop)
        {
            var lazyProp = _context.GetLazyStringForFieldWithCaching(prop);
            WriteString(lazyProp);
            EnsureBuffer(1);
            _buffer[_pos++] = Colon;
        }

        public void WriteInteger(long val)
        {
            if (val == 0)
            {
                EnsureBuffer(1);
                _buffer[_pos++] = (byte)'0';
                return;
            }

            var localBuffer = _parserAuxiliarMemory.Address;

            int idx = 0;
            var negative = false;
            var isLongMin = false;
            if (val < 0)
            {
                negative = true;
                if (val == long.MinValue)
                {
                    isLongMin = true;
                    val = long.MaxValue;
                }
                else
                    val = -val; // value is positive now.
            }

            do
            {
                var v = val % 10;
                if (isLongMin)
                {
                    isLongMin = false;
                    v += 1;
                }

                localBuffer[idx++] = (byte)('0' + v);
                val /= 10;
            }
            while (val != 0);

            if (negative)
                localBuffer[idx++] = (byte)'-';

            EnsureBuffer(idx);

            var buffer = _buffer;
            int auxPos = _pos;

            do
                buffer[auxPos++] = localBuffer[--idx];
            while (idx > 0);

            _pos = auxPos;
        }

        public void WriteDouble(LazyNumberValue val)
        {
            if (val.IsNaN())
            {
                WriteBufferFor(NaNBuffer);
                return;
            }

            if (val.IsPositiveInfinity())
            {
                WriteBufferFor(PositiveInfinityBuffer);
                return;
            }

            if (val.IsNegativeInfinity())
            {
                WriteBufferFor(NegativeInfinityBuffer);
                return;
            }

            var lazyStringValue = val.Inner;
            EnsureBuffer(lazyStringValue.Size);
            WriteRawString(lazyStringValue.Buffer, lazyStringValue.Size);
        }

        public void WriteBufferFor(byte[] buffer)
        {
            EnsureBuffer(buffer.Length);
            for (int i = 0; i < buffer.Length; i++)
            {
                _buffer[_pos++] = buffer[i];
            }
        }

        public void WriteDouble(double val)
        {
            if (double.IsNaN(val))
            {
                WriteBufferFor(NaNBuffer);
                return;
            }

            if (double.IsPositiveInfinity(val))
            {
                WriteBufferFor(PositiveInfinityBuffer);
                return;
            }

            if (double.IsNegativeInfinity(val))
            {
                WriteBufferFor(NegativeInfinityBuffer);
                return;
            }

            using (var lazyStr = _context.GetLazyString(val.ToString(CultureInfo.InvariantCulture)))
            {
                EnsureBuffer(lazyStr.Size);
                WriteRawString(lazyStr.Buffer, lazyStr.Size);
            }
        }

        public void Dispose()
        {
            try
            {
                Flush();
            }
            catch (ObjectDisposedException)
            {
                //we are disposing, so this exception doesn't matter
            }
            finally
            {
                _returnBuffer.Dispose();
                _context.ReturnMemory(_parserAuxiliarMemory);
            }
        }

        public void WriteNewLine()
        {
            EnsureBuffer(2);
            _buffer[_pos++] = (byte)'\r';
            _buffer[_pos++] = (byte)'\n';
        }

        public void WriteStream(Stream stream)
        {
            Flush();

            while (true)
            {
                _pos = stream.Read(_pinnedBuffer.Buffer.Array, _pinnedBuffer.Buffer.Offset, _pinnedBuffer.Buffer.Count);
                if (_pos == 0)
                    break;

                Flush();
            }
        }

        public void WriteMemoryChunk(IntPtr ptr, int size)
        {
            Flush();
            var p = (byte*)ptr.ToPointer();
            var leftToWrite = size;
            var totalWritten = 0;
            while (leftToWrite > 0)
            {
                var toWrite = Math.Min(JsonOperationContext.ManagedPinnedBuffer.Size, leftToWrite);
                Memory.Copy(_buffer, p + totalWritten, toWrite);
                _pos += toWrite;
                totalWritten += toWrite;
                leftToWrite -= toWrite;
                Flush();
            }
        }
    }
}
