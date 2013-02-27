﻿//  Copyright 2011-2012 Marc Fletcher, Matthew Dean
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
//  A commercial license of this software can also be purchased. 
//  Please see <http://www.networkcommsdotnet.com/licenses> for details.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Security.Cryptography;

namespace DPSBase
{
    /// <summary>
    /// A wrapper around a stream to ensure it can be accessed in a thread safe way. The .net implementation of Stream.Synchronized is not suitable on its own.
    /// </summary>
    public class ThreadSafeStream : IDisposable
    {
        private Stream stream;
        private object streamLocker = new object();

        /// <summary>
        /// If true the internal stream will be disposed once the data has been written to the network
        /// </summary>
        public bool CloseStreamAfterSend { get; private set; }

        /// <summary>
        /// Create a thread safe stream. Once any actions are complete the stream must be correctly disposed by the user.
        /// </summary>
        /// <param name="stream">The stream to make thread safe</param>
        public ThreadSafeStream(Stream stream)
        {
            this.CloseStreamAfterSend = false;

            if (stream.Length > int.MaxValue)
                throw new NotImplementedException("Streams larger than 2GB not yet supported.");

            this.stream = stream;
        }

        /// <summary>
        /// Create a thread safe stream.
        /// </summary>
        /// <param name="stream">The stream to make thread safe.</param>
        /// <param name="closeStreamAfterSend">If true the provided stream will be disposed once data has been written to the network. If false the stream must be disposed of correctly by the user</param>
        public ThreadSafeStream(Stream stream, bool closeStreamAfterSend)
        {
            this.CloseStreamAfterSend = closeStreamAfterSend;

            if (stream.Length > int.MaxValue)
                throw new NotImplementedException("Streams larger than 2GB not yet supported.");

            this.stream = stream;
        }

        /// <summary>
        /// The total length of the internal stream
        /// </summary>
        public long Length
        {
            get { lock (streamLocker) return stream.Length; }
        }

        /// <summary>
        /// The current position of the internal stream
        /// </summary>
        public long Position
        {
            get { lock (streamLocker) return stream.Position; }
        }

        /// <summary>
        /// Returns data from entire Stream
        /// </summary>
        /// <param name="numberZeroBytesPrefex">If non zero will append N 0 value bytes to the start of the returned array</param>
        /// <returns></returns>
        public byte[] ToArray(int numberZeroBytesPrefex = 0)
        {
            lock (streamLocker)
            {
                stream.Seek(0, SeekOrigin.Begin);
                byte[] returnData = new byte[stream.Length + numberZeroBytesPrefex];
                stream.Read(returnData, numberZeroBytesPrefex, returnData.Length - numberZeroBytesPrefex);
                return returnData;
            }
        }

        /// <summary>
        /// Returns data from the specified portion of Stream
        /// </summary>
        /// <param name="start">The start position of the desired bytes</param>
        /// <param name="length">The total number of desired bytes</param>
        /// <param name="numberZeroBytesPrefex">If non zero will append N 0 value bytes to the start of the returned array</param>
        /// <returns></returns>
        public byte[] ToArray(int start, int length, int numberZeroBytesPrefex = 0)
        {
            lock (streamLocker)
            {
                if (start + length > stream.Length)
                    throw new ArgumentOutOfRangeException("Provided start and length parameters reference past the end of the available stream.");

                stream.Seek(start, SeekOrigin.Begin);
                byte[] returnData = new byte[length + numberZeroBytesPrefex];
                stream.Read(returnData, numberZeroBytesPrefex, returnData.Length - numberZeroBytesPrefex);
                return returnData;
            }
        }

        /// <summary>
        /// Return the MD5 hash of the current <see cref="ThreadSafeStream"/> as a string
        /// </summary>
        /// <returns></returns>
        public string MD5CheckSum()
        {
            lock (streamLocker)
            {
                stream.Seek(0, SeekOrigin.Begin);
                HashAlgorithm md5;

#if WINDOWS_PHONE
                    md5 = new DPSBase.MD5.MD5Managed();
#else
                    md5 = System.Security.Cryptography.MD5.Create();
#endif

                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "");
            }
        }

        /// <summary>
        /// Writes all provided data to the internal stream starting at the provided position with the stream
        /// </summary>
        /// <param name="data"></param>
        /// <param name="startPosition"></param>
        public void Write(byte[] data, int startPosition)
        {
            lock (streamLocker)
            {
                stream.Seek(startPosition, SeekOrigin.Begin);

                System.Threading.AutoResetEvent wait = new System.Threading.AutoResetEvent(false); 

                //stream.Write(data, 0, data.Length);
                stream.BeginWrite(data, 0, data.Length, new AsyncCallback(result =>
                    {
                        stream.EndWrite(result);
                        wait.Set();
                    }), null);

                //Choose a timeout that allows upto 5.2 seconds per MB. Corresponds to minimum write speed of 0.2MB/s
                int waitTimeMS = (int)(data.Length * 5E-3);
                if (!wait.WaitOne(waitTimeMS))
                    throw new TimeoutException("Timeout waiting to write " + (data.Length/1024.0).ToString("0.0") + "KB after " + (waitTimeMS / 1000.0).ToString("0.0") + " seconds.");
                
                stream.Flush();
            }
        }

        /// <summary>
        /// Copies data specified by start and length properties from internal stream to the provided stream.
        /// </summary>
        /// <param name="destinationStream">The destination stream to write to</param>
        /// <param name="startPosition"></param>
        /// <param name="length"></param>
        public void CopyTo(Stream destinationStream, int startPosition, int length)
        {
            //Initialise the buffer at either the total length or 8KB, which ever is smallest
            //This is the largest copy buffer we can use without mono putting the buffer on the LOH
            //According to this http://www.mono-project.com/Working_With_SGen
            //Performance can be improved if buffer is increased to 80KB
            byte[] buffer = new byte[Math.Min(8000, length)];

            lock (streamLocker)
            {
                //Make sure we start in the write place
                stream.Seek(startPosition, SeekOrigin.Begin);
                int totalBytesCopied = 0;
                while (true)
                {
                    int bytesRemaining = length - totalBytesCopied;

                    if (bytesRemaining == 0)
                        break;

                    int read = stream.Read(buffer, 0, (buffer.Length > bytesRemaining ? bytesRemaining : buffer.Length));

                    if (read <= 0)
                        break;

                    if (!destinationStream.CanWrite) throw new Exception("Unable to write to provided destinationStream.");

                    destinationStream.Write(buffer, 0, read);
                    totalBytesCopied += read;
                }
            }
        }

        /// <summary>
        /// Call Close on the internal stream
        /// </summary>
        public void Dispose()
        {
            lock (streamLocker) stream.Close();
        }

        /// <summary>
        /// Call Close on the internal stream
        /// </summary>
        public void Close()
        {
            lock (streamLocker) stream.Close();
        }
    }
}
