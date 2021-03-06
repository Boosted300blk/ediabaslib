﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Android.Bluetooth;
using Java.Util;

namespace EdiabasLib
{
    public class EdBluetoothInterface : EdBluetoothInterfaceBase
    {
        public static readonly string[] Elm327InitCommands = EdElmInterface.Elm327InitCommands;
        public const string PortId = "BLUETOOTH";
        public const string Elm327Tag = "ELM327";
        public const string RawTag = "RAW";
        private static readonly UUID SppUuid = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB");
        private static readonly long TickResolMs = Stopwatch.Frequency / 1000;
        private const int ReadTimeoutOffsetLong = 1000;
        private const int ReadTimeoutOffsetShort = 100;
        protected const int EchoTimeout = 500;
        private static BluetoothSocket _bluetoothSocket;
        private static Stream _bluetoothInStream;
        private static Stream _bluetoothOutStream;
        private static bool _rawMode;
        private static bool _elm327Device;
        private static bool _reconnectRequired;
        private static string _connectPort;
        private static EdElmInterface _edElmInterface;

        static EdBluetoothInterface()
        {
        }

        public static BluetoothSocket BluetoothSocket => _bluetoothSocket;

        public static bool InterfaceConnect(string port, object parameter)
        {
            if (_bluetoothSocket != null)
            {
                return true;
            }
            FastInit = false;
            ConvertBaudResponse = false;
            AutoKeyByteResponse = false;
            AdapterType = -1;
            AdapterVersion = -1;
            LastCommTick = DateTime.MinValue.Ticks;

            if (!port.StartsWith(PortId, StringComparison.OrdinalIgnoreCase))
            {
                InterfaceDisconnect();
                return false;
            }
            BluetoothAdapter bluetoothAdapter = BluetoothAdapter.DefaultAdapter;
            if (bluetoothAdapter == null)
            {
                return false;
            }
            _rawMode = false;
            _elm327Device = false;
            _connectPort = port;
            _reconnectRequired = false;
            try
            {
                BluetoothDevice device;
                string portData = port.Remove(0, PortId.Length);
                if ((portData.Length > 0) && (portData[0] == ':'))
                {   // special id
                    string addr = portData.Remove(0, 1);
                    string[] stringList = addr.Split('#', ';');
                    if (stringList.Length == 0)
                    {
                        InterfaceDisconnect();
                        return false;
                    }
                    device = bluetoothAdapter.GetRemoteDevice(stringList[0]);
                    if (stringList.Length > 1)
                    {
                        if (string.Compare(stringList[1], Elm327Tag, StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            _elm327Device = true;
                        }
                        else if (string.Compare(stringList[1], RawTag, StringComparison.OrdinalIgnoreCase) == 0)
                        {
                            _rawMode = true;
                        }
                    }
                }
                else
                {
                    InterfaceDisconnect();
                    return false;
                }
                if (device == null)
                {
                    InterfaceDisconnect();
                    return false;
                }
                bluetoothAdapter.CancelDiscovery();

                _bluetoothSocket = device.CreateRfcommSocketToServiceRecord(SppUuid);
                try
                {
                    _bluetoothSocket.Connect();
                }
                catch (Exception)
                {
                    try
                    {
                        // sometimes the second connect is working
                        _bluetoothSocket.Connect();
                    }
                    catch (Exception)
                    {
                        _bluetoothSocket.Close();
                        _bluetoothSocket = null;
                    }
                }

                if (_bluetoothSocket == null)
                {
                    // this socket sometimes looses data for long telegrams
                    IntPtr createRfcommSocket = Android.Runtime.JNIEnv.GetMethodID(device.Class.Handle,
                        "createRfcommSocket", "(I)Landroid/bluetooth/BluetoothSocket;");
                    if (createRfcommSocket == IntPtr.Zero)
                    {
                        throw new Exception("No createRfcommSocket");
                    }
                    IntPtr rfCommSocket = Android.Runtime.JNIEnv.CallObjectMethod(device.Handle,
                        createRfcommSocket, new Android.Runtime.JValue(1));
                    if (rfCommSocket == IntPtr.Zero)
                    {
                        throw new Exception("No rfCommSocket");
                    }
                    _bluetoothSocket = Java.Lang.Object.GetObject<BluetoothSocket>(rfCommSocket, Android.Runtime.JniHandleOwnership.TransferLocalRef);
                    _bluetoothSocket.Connect();
                }
                Thread.Sleep(500);

                _bluetoothInStream = _bluetoothSocket.InputStream;
                _bluetoothOutStream = _bluetoothSocket.OutputStream;

                if (_elm327Device)
                {
                    _edElmInterface = new EdElmInterface(Ediabas, _bluetoothInStream, _bluetoothOutStream);
                    if (!_edElmInterface.Elm327Init())
                    {
                        InterfaceDisconnect();
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                InterfaceDisconnect ();
                return false;
            }
            return true;
        }

        public static bool InterfaceDisconnect()
        {
            bool result = true;
            if (_edElmInterface != null)
            {
                _edElmInterface.Dispose();
                _edElmInterface = null;
            }
            try
            {
                if (_bluetoothInStream != null)
                {
                    _bluetoothInStream.Close();
                    _bluetoothInStream = null;
                }
            }
            catch (Exception)
            {
                result = false;
            }
            try
            {
                if (_bluetoothOutStream != null)
                {
                    _bluetoothOutStream.Close();
                    _bluetoothOutStream = null;
                }
            }
            catch (Exception)
            {
                result = false;
            }
            try
            {
                if (_bluetoothSocket != null)
                {
                    _bluetoothSocket.Close();
                    _bluetoothSocket = null;
                }
            }
            catch (Exception)
            {
                result = false;
            }
            return result;
        }

        public static EdInterfaceObd.InterfaceErrorResult InterfaceSetConfig(EdInterfaceObd.Protocol protocol, int baudRate, int dataBits, EdInterfaceObd.SerialParity parity, bool allowBitBang)
        {
            if (_bluetoothSocket == null)
            {
                return EdInterfaceObd.InterfaceErrorResult.ConfigError;
            }
            CurrentProtocol = protocol;
            CurrentBaudRate = baudRate;
            CurrentWordLength = dataBits;
            CurrentParity = parity;
            FastInit = false;
            ConvertBaudResponse = false;
            return EdInterfaceObd.InterfaceErrorResult.NoError;
        }

        public static bool InterfaceSetDtr(bool dtr)
        {
            if (_bluetoothSocket == null)
            {
                return false;
            }
            return true;
        }

        public static bool InterfaceSetRts(bool rts)
        {
            if (_bluetoothSocket == null)
            {
                return false;
            }
            return true;
        }

        public static bool InterfaceGetDsr(out bool dsr)
        {
            dsr = true;
            if (_bluetoothSocket == null)
            {
                return false;
            }
            return true;
        }

        public static bool InterfaceSetBreak(bool enable)
        {
            return false;
        }

        public static bool InterfaceSetInterByteTime(int time)
        {
            InterByteTime = time;
            return true;
        }

        public static bool InterfaceSetCanIds(int canTxId, int canRxId, EdInterfaceObd.CanFlags canFlags)
        {
            CanTxId = canTxId;
            CanRxId = canRxId;
            CanFlags = canFlags;
            return true;
        }

        public static bool InterfacePurgeInBuffer()
        {
            if ((_bluetoothSocket == null) || (_bluetoothInStream == null))
            {
                return false;
            }
            if (_elm327Device)
            {
                if (_edElmInterface == null)
                {
                    return false;
                }
                return _edElmInterface.InterfacePurgeInBuffer();
            }
            try
            {
                FlushReceiveBuffer();
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        public static bool InterfaceAdapterEcho()
        {
            return false;
        }

        public static bool InterfaceHasPreciseTimeout()
        {
            return false;
        }

        public static bool InterfaceHasAutoBaudRate()
        {
            return true;
        }

        public static bool InterfaceHasAutoKwp1281()
        {
            if (!UpdateAdapterInfo())
            {
                return false;
            }
            if (AdapterVersion < 0x0008)
            {
                return false;
            }
            return true;
        }

        public static bool InterfaceSendData(byte[] sendData, int length, bool setDtr, double dtrTimeCorr)
        {
            ConvertBaudResponse = false;
            AutoKeyByteResponse = false;
            if ((_bluetoothSocket == null) || (_bluetoothOutStream == null))
            {
                return false;
            }
            if (_elm327Device)
            {
                if ((CurrentProtocol != EdInterfaceObd.Protocol.Uart) ||
                    (CurrentBaudRate != 115200) || (CurrentWordLength != 8) || (CurrentParity != EdInterfaceObd.SerialParity.None))
                {
                    return false;
                }
                if (_edElmInterface == null)
                {
                    return false;
                }
                if (_edElmInterface.StreamFailure)
                {
                    Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "Reconnecting");
                    InterfaceDisconnect();
                    if (!InterfaceConnect(_connectPort, null))
                    {
                        _edElmInterface.StreamFailure = true;
                        return false;
                    }
                }
                return _edElmInterface.InterfaceSendData(sendData, length, setDtr, dtrTimeCorr);
            }
            if (_reconnectRequired)
            {
                Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "Reconnecting");
                InterfaceDisconnect();
                if (!InterfaceConnect(_connectPort, null))
                {
                    _reconnectRequired = true;
                    return false;
                }
                _reconnectRequired = false;
            }
            try
            {
                if ((CurrentProtocol == EdInterfaceObd.Protocol.Tp20) ||
                    (CurrentProtocol == EdInterfaceObd.Protocol.IsoTp))
                {
                    UpdateAdapterInfo();
                    byte[] adapterTel = CreateCanTelegram(sendData, length);
                    if (adapterTel == null)
                    {
                        return false;
                    }
                    _bluetoothOutStream.Write(adapterTel, 0, adapterTel.Length);
                    LastCommTick = Stopwatch.GetTimestamp();
                    UpdateActiveSettings();
                    return true;
                }
                if (_rawMode || (CurrentBaudRate == 115200))
                {   // BMW-FAST
                    _bluetoothOutStream.Write(sendData, 0, length);
                    LastCommTick = Stopwatch.GetTimestamp();
                    // remove echo
                    byte[] receiveData = new byte[length];
                    if (!InterfaceReceiveData(receiveData, 0, length, EchoTimeout, EchoTimeout, null))
                    {
                        return false;
                    }
                    for (int i = 0; i < length; i++)
                    {
                        if (receiveData[i] != sendData[i])
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    UpdateAdapterInfo();
                    byte[] adapterTel = CreateAdapterTelegram(sendData, length, setDtr);
                    FastInit = false;
                    if (adapterTel == null)
                    {
                        return false;
                    }
                    _bluetoothOutStream.Write(adapterTel, 0, adapterTel.Length);
                    LastCommTick = Stopwatch.GetTimestamp();
                    UpdateActiveSettings();
                }
            }
            catch (Exception ex)
            {
                Ediabas?.LogFormat(EdiabasNet.EdLogLevel.Ifh, "*** Stream failure: {0}", ex.Message);
                _reconnectRequired = true;
                return false;
            }
            return true;
        }

        public static bool InterfaceReceiveData(byte[] receiveData, int offset, int length, int timeout, int timeoutTelEnd, EdiabasNet ediabasLog)
        {
            bool convertBaudResponse = ConvertBaudResponse;
            bool autoKeyByteResponse = AutoKeyByteResponse;
            ConvertBaudResponse = false;
            AutoKeyByteResponse = false;

            if ((_bluetoothSocket == null) || (_bluetoothInStream == null))
            {
                return false;
            }
            if (_elm327Device)
            {
                if (_edElmInterface == null)
                {
                    return false;
                }
                return _edElmInterface.InterfaceReceiveData(receiveData, offset, length, timeout, timeoutTelEnd, ediabasLog);
            }
            int timeoutOffset = ReadTimeoutOffsetLong;
            if (((Stopwatch.GetTimestamp() - LastCommTick) < 100 * TickResolMs) && (timeout < 100))
            {
                timeoutOffset = ReadTimeoutOffsetShort;
            }
            //Ediabas?.LogFormat(EdiabasNet.EdLogLevel.Ifh, "Timeout offset {0}", timeoutOffset);
            timeout += timeoutOffset;
            timeoutTelEnd += timeoutOffset;
            try
            {
                if (!_rawMode && SettingsUpdateRequired())
                {
                    Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "InterfaceReceiveData, update settings");
                    UpdateAdapterInfo();
                    byte[] adapterTel = CreatePulseTelegram(0, 0, 0, false, false, 0);
                    if (adapterTel == null)
                    {
                        return false;
                    }
                    _bluetoothOutStream.Write(adapterTel, 0, adapterTel.Length);
                    LastCommTick = Stopwatch.GetTimestamp();
                    UpdateActiveSettings();
                }
                if (convertBaudResponse && length == 2)
                {
                    Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "Convert baud response");
                    length = 1;
                    AutoKeyByteResponse = true;
                }
                int recLen = 0;
                long startTime = Stopwatch.GetTimestamp();
                while (recLen < length)
                {
                    int currTimeout = (recLen == 0) ? timeout : timeoutTelEnd;
                    if (_bluetoothInStream.IsDataAvailable())
                    {
                        int bytesRead = _bluetoothInStream.Read (receiveData, offset + recLen, length - recLen);
                        if (bytesRead > 0)
                        {
                            LastCommTick = Stopwatch.GetTimestamp();
                        }
                        recLen += bytesRead;
                    }
                    if (recLen >= length)
                    {
                        break;
                    }
                    if ((Stopwatch.GetTimestamp() - startTime) > currTimeout * TickResolMs)
                    {
                        ediabasLog?.LogData(EdiabasNet.EdLogLevel.Ifh, receiveData, offset, recLen, "Rec ");
                        return false;
                    }
                    Thread.Sleep(10);
                }
                if (convertBaudResponse)
                {
                    ConvertStdBaudResponse(receiveData, offset);
                }
                if (autoKeyByteResponse && length == 2)
                {   // auto key byte response for old adapter
                    Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "Auto key byte response");
                    byte[] keyByteResponse = { (byte)~receiveData[offset + 1] };
                    byte[] adapterTel = CreateAdapterTelegram(keyByteResponse, keyByteResponse.Length, true);
                    if (adapterTel == null)
                    {
                        return false;
                    }
                    _bluetoothOutStream.Write(adapterTel, 0, adapterTel.Length);
                    LastCommTick = Stopwatch.GetTimestamp();
                }
            }
            catch (Exception ex)
            {
                Ediabas?.LogFormat(EdiabasNet.EdLogLevel.Ifh, "*** Stream failure: {0}", ex.Message);
                _reconnectRequired = true;
                return false;
            }
            return true;
        }

        public static bool InterfaceSendPulse(UInt64 dataBits, int length, int pulseWidth, bool setDtr, bool bothLines, int autoKeyByteDelay)
        {
            ConvertBaudResponse = false;
            if ((_bluetoothSocket == null) || (_bluetoothOutStream == null))
            {
                return false;
            }
            if (_elm327Device)
            {
                return false;
            }
            if (_reconnectRequired)
            {
                Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "Reconnecting");
                InterfaceDisconnect();
                if (!InterfaceConnect(_connectPort, null))
                {
                    _reconnectRequired = true;
                    return false;
                }
                _reconnectRequired = false;
            }
            try
            {
                UpdateAdapterInfo();
                FastInit = IsFastInit(dataBits, length, pulseWidth);
                if (FastInit)
                {   // send next telegram with fast init
                    return true;
                }
                byte[] adapterTel = CreatePulseTelegram(dataBits, length, pulseWidth, setDtr, bothLines, autoKeyByteDelay);
                if (adapterTel == null)
                {
                    return false;
                }
                _bluetoothOutStream.Write(adapterTel, 0, adapterTel.Length);
                LastCommTick = Stopwatch.GetTimestamp();
                UpdateActiveSettings();
                Thread.Sleep(pulseWidth * length);
            }
            catch (Exception ex)
            {
                Ediabas?.LogFormat(EdiabasNet.EdLogLevel.Ifh, "*** Stream failure: {0}", ex.Message);
                _reconnectRequired = true;
                return false;
            }
            return true;
        }

        private static void FlushReceiveBuffer()
        {
            _bluetoothInStream.Flush();
            while (_bluetoothInStream.IsDataAvailable())
            {
                _bluetoothInStream.ReadByte();
            }
        }

        // ReSharper disable once UnusedMethodReturnValue.Local
        private static bool UpdateAdapterInfo(bool forceUpdate = false)
        {
            if ((_bluetoothSocket == null) || (_bluetoothOutStream == null))
            {
                return false;
            }
            if (_elm327Device)
            {
                return false;
            }
            if (!forceUpdate && AdapterType >= 0)
            {   // only read once
                return true;
            }
            AdapterType = -1;
            try
            {
                const int versionRespLen = 9;
                byte[] identTel = { 0x82, 0xF1, 0xF1, 0xFD, 0xFD, 0x5E };
                FlushReceiveBuffer();
                _bluetoothOutStream.Write(identTel, 0, identTel.Length);
                LastCommTick = Stopwatch.GetTimestamp();

                List<byte> responseList = new List<byte>();
                long startTime = Stopwatch.GetTimestamp();
                for (; ; )
                {
                    while (_bluetoothInStream.IsDataAvailable())
                    {
                        int data = _bluetoothInStream.ReadByte();
                        if (data >= 0)
                        {
                            LastCommTick = Stopwatch.GetTimestamp();
                            responseList.Add((byte)data);
                            startTime = Stopwatch.GetTimestamp();
                        }
                    }
                    if (responseList.Count >= identTel.Length + versionRespLen)
                    {
                        bool validEcho = !identTel.Where((t, i) => responseList[i] != t).Any();
                        if (!validEcho)
                        {
                            return false;
                        }
                        if (CalcChecksumBmwFast(responseList.ToArray(), identTel.Length, versionRespLen - 1) != responseList[identTel.Length + versionRespLen - 1])
                        {
                            return false;
                        }
                        AdapterType = responseList[identTel.Length + 5] + (responseList[identTel.Length + 4] << 8);
                        AdapterVersion = responseList[identTel.Length + 7] + (responseList[identTel.Length + 6] << 8);
                        break;
                    }
                    if (Stopwatch.GetTimestamp() - startTime > ReadTimeoutOffsetLong * TickResolMs)
                    {
                        if (responseList.Count >= identTel.Length)
                        {
                            bool validEcho = !identTel.Where((t, i) => responseList[i] != t).Any();
                            if (validEcho)
                            {
                                AdapterType = 0;
                            }
                        }
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Ediabas?.LogFormat(EdiabasNet.EdLogLevel.Ifh, "*** Stream failure: {0}", ex.Message);
                _reconnectRequired = true;
                return false;
            }

            return true;
        }
    }
}
