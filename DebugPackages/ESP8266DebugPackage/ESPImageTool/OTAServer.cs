using ESP8266DebugPackage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ESPImageTool
{
    class OTAServer
    {
        struct OTAImage
        {
            public byte[] Data;
            public string File;
        }

        public static void ServeOTAFiles(int port, ESP8266BinaryImage.ParsedHeader hdr, params string[] elfFiles)
        {
            TcpListener listener = new TcpListener(port);
            byte[] buffer = new byte[1024];
            OTAImage[] images = new OTAImage[2];

            foreach (var fn in elfFiles)
                if (fn != null)
                    using (var elfFile = new ELFFile(fn))
                    {
                        string status;
                        int appMode = ESP8266BinaryImage.DetectAppMode(elfFile, out status);
                        if (appMode == 0)
                        {
                            Console.WriteLine(fn + " is not an OTA ELF file. Skipping...");
                            continue;
                        }

                        var img = ESP8266BinaryImage.MakeBootloaderBasedImageFromELFFile(elfFile, hdr, appMode);
                        using (var ms = new MemoryStream())
                        {
                            img.Save(ms);
                            images[appMode - 1].Data = ms.ToArray();
                            images[appMode - 1].File = fn;
                        }
                    }


            Console.WriteLine($"Ready to serve the following files:");
            Console.WriteLine($"APP1: {images[0].File ?? "(none)"}");
            Console.WriteLine($"APP2: {images[1].File ?? "(none)"}");
            Console.WriteLine($"Waiting for connection on port {port}...");
            listener.Start();
            for (;;)
            {
                using (var sock = listener.AcceptSocket())
                {
                    Console.WriteLine($"Incoming connection from {(sock.RemoteEndPoint as IPEndPoint).Address}");

                    StringBuilder requestBuilder = new StringBuilder();
                    while (!requestBuilder.ToString().Contains("\r\n\r"))
                    {
                        int done = sock.Receive(buffer);
                        requestBuilder.Append(Encoding.UTF8.GetString(buffer, 0, done));
                    }

                    string request = requestBuilder.ToString();
                    string[] parts = request.Split(' ');
                    if (parts.Length < 3)
                        throw new Exception("Invalid HTTP request: " + request);

                    string url = parts[1];
                    Console.WriteLine("Received request for " + url);
                    int otaIndex = (url.ToLower().Contains("user2") ? 1 : 0);
                    if (images[otaIndex].Data == null)
                        throw new Exception($"No OTA image for app{otaIndex + 1} is provided. Please check your linker scripts.");

                    string reply = string.Format("HTTP/1.0 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {0}\r\n\r\n", images[otaIndex].Data.Length);
                    var r = Encoding.UTF8.GetBytes(reply);
                    sock.Send(r);

                    if (parts[0] == "GET")
                    {
                        Console.Write($"Serving {Path.GetFileName(images[otaIndex].File)}...\r\n");

                        using (var ms = new MemoryStream(images[otaIndex].Data))
                        {
                            int totalDone = 0;
                            for (;;)
                            {
                                int done = ms.Read(buffer, 0, buffer.Length);
                                if (done == 0)
                                    break;
                                sock.Send(buffer, done, SocketFlags.None);
                                totalDone += done;

                                int percent = (int)((totalDone * 100) / ms.Length);
                                int progress = percent / 5;
                                Console.Write($"\r[{new string('#', progress).PadRight(20)}] {percent}%");
                            }
                        }
                        Console.WriteLine("\r\nFile sent successfully\n");
                        break;
                    }
                }
            }
            listener.Stop();
        }
    }
}
