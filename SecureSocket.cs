using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SimpleReverseTunnel
{
    public class SecureSocket : IDisposable
    {
        public Socket InnerSocket { get; }
        private readonly byte[] _key;
        private int _sendIndex;
        private int _recvIndex;

        public SecureSocket(Socket socket, string password)
        {
            InnerSocket = socket;
            // 通过密码生成 SHA256 密钥，确保存储分布均匀
            using var sha256 = SHA256.Create();
            _key = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        }

        public bool Connected => InnerSocket.Connected;
        public EndPoint? RemoteEndPoint => InnerSocket.RemoteEndPoint;

        public async Task<int> ReceiveAsync(Memory<byte> buffer)
        {
            int read = await InnerSocket.ReceiveAsync(buffer, SocketFlags.None);
            if (read > 0)
            {
                ApplyXor(buffer.Span.Slice(0, read), ref _recvIndex);
            }
            return read;
        }

        public async Task SendAsync(ReadOnlyMemory<byte> buffer)
        {
            // 租用缓冲区以避免修改原始数据（XOR 是原地操作）
            byte[] temp = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.Span.CopyTo(temp);
                ApplyXor(temp.AsSpan(0, buffer.Length), ref _sendIndex);
                await InnerSocket.SendAsync(temp.AsMemory(0, buffer.Length), SocketFlags.None);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(temp);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ApplyXor(Span<byte> data, ref int keyIndex)
        {
            // 简单的字节 XOR 非常快，通常会被 JIT 自动向量化。
            // 显式 SIMD (Vector<byte>) 在这种滚动密钥窗口场景下实现较复杂。
            // 保持简单的流式加密行为。
            
            // 手动展开小循环
            int i = 0;
            int len = data.Length;
            int kLen = _key.Length;

            // 避免在循环中使用 % 运算符
            int kIdx = keyIndex % kLen;
            
            for (; i < len; i++)
            {
                data[i] ^= _key[kIdx];
                kIdx++;
                if (kIdx == kLen) kIdx = 0;
            }
            
            // 更新持久化索引
            keyIndex = (keyIndex + len) % int.MaxValue; // 安全回绕
        }

        public void Shutdown(SocketShutdown how) => InnerSocket.Shutdown(how);
        public void Close() => InnerSocket.Close();
        public void Dispose() => InnerSocket.Dispose();
    }
}
