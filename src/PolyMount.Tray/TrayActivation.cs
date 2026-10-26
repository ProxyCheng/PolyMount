using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtNetwork;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Qt local connections activate the existing tray on its UI thread.</summary>
internal sealed class TrayActivation : IDisposable
{
    private readonly QLocalServer _server = new();
    private readonly Action _activate;

    public static string ServerName(string settingsDirectory) => "PolyMount.Tray." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsDirectory.ToUpperInvariant())))[..24];

    public TrayActivation(string name, Action activate)
    {
        _activate = activate;
        _server.SetSocketOptions(QLocalServer.SocketOptions.UserAccessOption);
        _server.SetMaxPendingConnections(8);
        _server.NewConnection += AcceptConnections;
        // The caller owns the instance lock; no live instance can own this endpoint.
        QLocalServer.RemoveServer(name);
        if (!_server.Listen(name))
        {
            string error = _server.ErrorString();
            _server.DeleteLater();
            throw new IOException("Unable to start tray activation server: " + error);
        }
    }

    private void AcceptConnections()
    {
        while (_server.HasPendingConnections())
        {
            var socket = _server.NextPendingConnection();
            socket.Disconnected += socket.DeleteLater;
            _activate();
            using var reply = new QByteArray("activated\n");
            socket.Write(reply);
            socket.Flush();
            socket.DisconnectFromServer();
        }
    }

    public static bool ActivateExisting(string name)
    {
        var timer = Stopwatch.StartNew();
        // The owning process may still be between taking its lock and listening.
        while (timer.ElapsedMilliseconds < 3000)
        {
            using var socket = new QLocalSocket();
            socket.ConnectToServer(name);
            if (socket.WaitForConnected(200))
                return socket.BytesAvailable() > 0 || socket.WaitForReadyRead(2000);
            Thread.Sleep(50);
        }
        return false;
    }

    internal static void Smoke()
    {
        int activations = 0;
        using var server = new TrayActivation("PolyMount.Tray.Smoke." + Guid.NewGuid().ToString("N"), () => activations++);
        using var socket = new QLocalSocket();
        socket.ConnectToServer(server._server.ServerName());
        if (!socket.WaitForConnected(1000)) throw new InvalidOperationException("Qt activation socket did not connect");
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 1000 && socket.BytesAvailable() == 0) QCoreApplication.ProcessEvents();
        if (activations != 1 || socket.BytesAvailable() == 0)
            throw new InvalidOperationException("Qt tray activation was not acknowledged");
    }

    public void Dispose()
    {
        _server.Close();
        _server.DeleteLater();
    }
}
