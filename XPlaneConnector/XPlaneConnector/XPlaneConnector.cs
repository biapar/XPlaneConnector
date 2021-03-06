﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XPlaneConnector
{
    public class XPlaneConnector
    {
        public int CheckInterval_ms = 1000;
        public TimeSpan MaxDataRefAge = TimeSpan.FromSeconds(5);

        private UdpClient client;
        private IPEndPoint XPlaneEP;
        private CancellationTokenSource ts;
        private Task serverTask;
        private Task observerTask;

        public delegate void RawReceiveHandler(string raw);
        public event RawReceiveHandler OnRawReceive;

        public delegate void DataRefReceived(DataRefElement dataRef);
        public event DataRefReceived OnDataRefReceived;

        public delegate void LogHandler(string message);
        public event LogHandler OnLog;

        private List<DataRefElement> DataRefs;

        public DateTime LastReceive { get; internal set; }
        public byte[] LastBuffer { get; internal set; }
        public IPEndPoint LocalEP
        {
            get
            {
                return ((IPEndPoint)client.Client.LocalEndPoint);
            }
        }

        public XPlaneConnector(string ip = "127.0.0.1", int xplanePort = 49000)
        {
            XPlaneEP = new IPEndPoint(IPAddress.Parse(ip), xplanePort);
            DataRefs = new List<DataRefElement>();
        }
        public void Start()
        {
            client = new UdpClient();
            client.Connect(XPlaneEP.Address, XPlaneEP.Port);

            var server = new UdpClient(LocalEP);

            ts = new CancellationTokenSource();
            var token = ts.Token;

            serverTask = Task.Factory.StartNew(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var response = await server.ReceiveAsync();
                    var raw = Encoding.UTF8.GetString(response.Buffer);
                    LastReceive = DateTime.Now;
                    LastBuffer = response.Buffer;

                    OnRawReceive?.Invoke(raw);
                    ParseResponse(response.Buffer);
                }

                OnLog?.Invoke("Stopping server");
                server.Close();
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            observerTask = Task.Factory.StartNew(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    foreach (var dr in DataRefs)
                        if (dr.Age > MaxDataRefAge)
                            RequestDataRef(dr);

                    await Task.Delay(CheckInterval_ms);
                }

            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        public void Stop(int timeout = 5000)
        {
            if (client != null)
            {
                foreach (var dr in DataRefs)
                    Unsubscribe(dr.DataRef);

                if (ts != null)
                {
                    ts.Cancel();
                    Task.WaitAll(new[] { serverTask, observerTask }, timeout);
                    ts.Dispose();
                    ts = null;

                    client.Close();
                }
            }
        }
        private void ParseResponse(byte[] buffer)
        {
            var pos = 0;
            var header = Encoding.UTF8.GetString(buffer, pos, 4);
            pos += 5; // Including tailing 0

            if (header == "RREF") // Ignore other messages
            {
                while (pos < buffer.Length)
                {
                    var id = BitConverter.ToInt32(buffer, pos);
                    pos += 4;

                    try
                    {
                        var value = BitConverter.ToSingle(buffer, pos);
                        pos += 4;
                        foreach (var dr in DataRefs)
                            if (dr.Update(id, value))
                                OnDataRefReceived?.Invoke(dr);
                    }
                    catch (Exception ex)
                    {
                        var error = ex.Message;
                    }
                }
            }
        }

        public void SendCommand(XPlaneCommand command)
        {
            var dg = new XPDatagram();
            dg.Add("CMND");
            dg.Add(command.Command);

            client.Send(dg.Get(), dg.Len);
        }

        public void Subscribe(DataRefElement dataref, int frequency = -1, Action<DataRefElement, float> onchange = null)
        {
            if (onchange != null)
                dataref.OnValueChange += (e, v) => { onchange(e, v); };

            if (frequency > 0)
                dataref.Frequency = frequency;

            DataRefs.Add(dataref);
        }

        public void Subscribe(StringDataRefElement dataref, int frequency = -1, Action<StringDataRefElement, string> onchange = null)
        {
            //if (onchange != null)
            //    dataref.OnValueChange += (e, v) => { onchange(e, v); };

            //Subscribe((DataRefElement)dataref, frequency);

            dataref.OnValueChange += (e, v) => { onchange(e, v); };

            for (var c = 0; c < dataref.StringLenght; c++)
            {
                var arrayElementDataRef = new DataRefElement
                {
                    DataRef = $"{dataref.DataRef}[{c}]",
                    Description = ""
                };

                var currentIndex = c;
                Subscribe(arrayElementDataRef, frequency, (e, v) =>
                {
                    var character = Convert.ToChar(Convert.ToInt32(v));
                    dataref.Update(currentIndex, character);
                });
            }
        }

        private void Subscribe(DataRefElement dataref, int frequency = -1)
        {
        }

        private void RequestDataRef(DataRefElement element)
        {
            if (client != null)
            {
                var dg = new XPDatagram();
                dg.Add("RREF");
                dg.Add(element.Frequency);
                dg.Add(element.Id);
                dg.Add(element.DataRef);
                dg.FillTo(413);

                client.Send(dg.Get(), dg.Len);

                OnLog?.Invoke($"Requested {element.DataRef}@{element.Frequency}Hz with Id:{element.Id}");
            }
        }

        public void Unsubscribe(string dataref)
        {
            var dr = DataRefs.SingleOrDefault(d => d.DataRef == dataref);

            if (dr != null)
            {
                var dg = new XPDatagram();
                dg.Add("RREF");
                dg.Add(dr.Id);
                dg.Add(0);
                dg.Add(dataref);
                dg.FillTo(413);

                client.Send(dg.Get(), dg.Len);

                OnLog?.Invoke($"Unsubscribed from {dataref}");
            }
        }

        public void SetDataRefValue(DataRefElement dataref, float value)
        {
            SetDataRefValue(dataref.DataRef, value);
        }

        public void SetDataRefValue(string dataref, float value)
        {
            var dg = new XPDatagram();
            dg.Add("DREF");
            dg.Add(value);
            dg.Add(dataref);
            dg.FillTo(509);

            client.Send(dg.Get(), dg.Len);
        }
        public void SetDataRefValue(string dataref, string value)
        {
            var dg = new XPDatagram();
            dg.Add("DREF");
            dg.Add(value);
            dg.Add(dataref);
            dg.FillTo(509);

            client.Send(dg.Get(), dg.Len);
        }
        public void QuitXPlane()
        {
            var dg = new XPDatagram();
            dg.Add("QUIT");

            client.Send(dg.Get(), dg.Len);
        }
        public void Fail(int system)
        {
            var dg = new XPDatagram();
            dg.Add("FAIL");

            dg.Add(system.ToString());

            client.Send(dg.Get(), dg.Len);
        }
        public void Recover(int system)
        {
            var dg = new XPDatagram();
            dg.Add("RECO");

            dg.Add(system.ToString());

            client.Send(dg.Get(), dg.Len);
        }
    }
}
