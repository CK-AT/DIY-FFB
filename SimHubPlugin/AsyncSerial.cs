using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Linq;

namespace ProtbufTest
{
    public class AsyncSerial
    {
        SerialPort port;
        BufferBlock<byte> rx_fifo = new BufferBlock<byte>();
        CancellationTokenSource cts = new CancellationTokenSource();
        bool _auto_reconnect = false;
        private readonly object _write_lock = new object();

        public bool IsOpen { get { return port.IsOpen; } }

        public string PortName {
            get { return port.PortName; }
            set { port.PortName = value; }
        }

        public bool RtsEnable { set {  port.RtsEnable = value; } }
        public bool DtrEnable { set { port.DtrEnable = value; } }

        public AsyncSerial(string port_name, Int32 baud_rate)
        {
            port = new SerialPort(port_name, baud_rate);
        }
        
        public bool Open(bool auto_reconnect=false)
        {
            _auto_reconnect=auto_reconnect;
            try
            {
                // DTR must be asserted for the native-USB build. The firmware's
                // USBCDC::write bails out on !tud_cdc_n_connected(), and TinyUSB
                // derives "connected" from the DTR line alone -- so with .NET's
                // default DtrEnable=false the device silently discards every byte
                // and the port looks dead. Irrelevant over the old CH343 UART
                // bridge, which is why this was never needed before.
                //
                // Set before Open() so it is applied as part of the open, leaving
                // no window where the device considers the host absent.
                //
                // RTS is deliberately NOT touched. USBCDC::_onLineState runs the
                // esptool reset state machine over DTR/RTS transitions --
                // (!dtr,rts) -> (dtr,rts) -> (dtr,!rts) -> (!dtr,!rts) reboots the
                // chip into the bootloader. Raising DTR alone cannot walk that
                // sequence; raising both would sit one transition away from it.
                port.DtrEnable = true;
                port.Open();
                cts.Cancel();
                cts = new CancellationTokenSource();
                Task.Run(async () => await RxTask(cts.Token));
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            return true;
        }

        private async Task RxTask(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var chunk = new byte[256];
                    var num_bytes = await port.BaseStream.ReadAsync(chunk, 0, chunk.Length, token);
                    foreach (var item in chunk.Take(num_bytes))
                    {
                        rx_fifo.Post(item);
                    }
                }
                catch (Exception) { }
            }
        }

        public async Task<byte[]> ReceiveData(int max_size=500, int timeout=30)
        {
            if (!port.IsOpen && _auto_reconnect)
            {
                Open(true);
            }
            var ms = new MemoryStream();
            int num_bytes = 0;
            // use timeout for the first byte but reduce to 2ms for successive bytes
            TimeSpan curr_timeout = TimeSpan.FromMilliseconds(timeout);
            try
            {
                while (num_bytes < max_size)
                {
                    var item = await rx_fifo.ReceiveAsync(curr_timeout);
                    ms.WriteByte(item);
                    num_bytes++;
                    curr_timeout = TimeSpan.FromMilliseconds(3);
                }
            } catch (TimeoutException)
            {

            }
            return ms.ToArray();
        }

        public async Task<byte[]> ReceiveDataTill(byte break_char = 0x00, int max_size = 500, CancellationToken token = new CancellationToken())
        {
            if (!port.IsOpen && _auto_reconnect)
            {
                Open(true);
            }
            var ms = new MemoryStream();
            int num_bytes = 0;
            bool break_char_received = false;
            try
            {
                while (num_bytes < max_size)
                {
                    var item = await rx_fifo.ReceiveAsync(token);
                    if (item != 0)
                    {
                        ms.WriteByte(item);
                        num_bytes++;
                        break_char_received = true;
                    }
                    else if (break_char_received)
                    {
                        ms.WriteByte(item);
                        break;
                    }
                }
            }
            catch (TimeoutException) { }
            return ms.ToArray();
        }

        public bool WriteData(byte[] data)
        {
            if (!port.IsOpen && _auto_reconnect)
            {
                if (!Open(true)) return false;
            }
            if (port.IsOpen)
            {
                lock (_write_lock)
                {
                    port.Write(data, 0, data.Length);
                }
                return true;
            }
            return false;
        }

        public void Close()
        {
            _auto_reconnect = false;
            cts.Cancel();
            port.Close();
        }
    }
}
