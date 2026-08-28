// Minimal Discord local-RPC client (named pipe discord-ipc-N).
// Frame format: [int32 opcode][int32 length][utf8 json], little-endian.
// Opcodes: 0=Handshake, 1=Frame, 2=Close, 3=Ping, 4=Pong.
using System;
using System.IO.Pipes;
using System.Text;

public class DiscordRpc
{
    NamedPipeClientStream pipe;

    public bool Connected { get { return pipe != null && pipe.IsConnected; } }

    public bool Connect(string clientId)
    {
        Close();
        for (int i = 0; i < 10; i++)
        {
            NamedPipeClientStream p = null;
            try
            {
                p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                p.Connect(300);
                pipe = p;
                if (!Send(0, "{\"v\":1,\"client_id\":\"" + clientId + "\"}")) continue;
                string ready = Read(3000);
                if (ready != null && ready.Contains("READY")) return true;
                Close();
            }
            catch
            {
                if (p != null) { try { p.Dispose(); } catch { } }
                pipe = null;
            }
        }
        return false;
    }

    public bool Send(int op, string json)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] buf = new byte[8 + data.Length];
            BitConverter.GetBytes(op).CopyTo(buf, 0);
            BitConverter.GetBytes(data.Length).CopyTo(buf, 4);
            data.CopyTo(buf, 8);
            pipe.Write(buf, 0, buf.Length);
            pipe.Flush();
            return true;
        }
        catch { Close(); return false; }
    }

    byte[] ReadExact(int count, int timeoutMs)
    {
        byte[] buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            var t = pipe.ReadAsync(buf, got, count - got);
            if (!t.Wait(timeoutMs)) { Close(); return null; }
            if (t.Result <= 0) { Close(); return null; }
            got += t.Result;
        }
        return buf;
    }

    public string Read(int timeoutMs)
    {
        try
        {
            byte[] header = ReadExact(8, timeoutMs);
            if (header == null) return null;
            int op = BitConverter.ToInt32(header, 0);
            int len = BitConverter.ToInt32(header, 4);
            byte[] payload = ReadExact(len, timeoutMs);
            if (payload == null) return null;
            string json = Encoding.UTF8.GetString(payload);
            if (op == 3) { Send(4, json); return Read(timeoutMs); }   // ping -> pong
            if (op == 2) { Close(); return null; }                    // server closed
            return json;
        }
        catch { Close(); return null; }
    }

    public string Request(string json, int timeoutMs)
    {
        if (!Send(1, json)) return null;
        return Read(timeoutMs);
    }

    public void Close()
    {
        if (pipe != null) { try { pipe.Dispose(); } catch { } pipe = null; }
    }
}
