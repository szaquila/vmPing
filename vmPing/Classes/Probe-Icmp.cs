﻿using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace vmPing.Classes
{
    public partial class Probe
    {
        private async void PerformIcmpProbe(CancellationToken cancellationToken)
        {
            InitializeProbe();
            await Application.Current.Dispatcher.BeginInvoke(
                new Action(() => AddHistory($"*** Pinging {Hostname}:")));

            if (await IsHostInvalid(Hostname, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                StopProbe(ProbeStatus.Error);
                return;
            }

            using (var ping = new Ping())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Send ping.
                        Statistics.Sent++;
                        Statistics.Rate = (float)Math.Round((float)Statistics.Lost * 100 / (float)Statistics.Sent, 2);
                        PingReply reply = await ping.SendPingAsync(
                            hostNameOrAddress: Hostname,
                            timeout: ApplicationOptions.PingTimeout,
                            buffer: ApplicationOptions.Buffer,
                            options: ApplicationOptions.GetPingOptions);
                        if (cancellationToken.IsCancellationRequested) return;

                        // Reply received.
                        if (reply.Status == IPStatus.Success)
                        {
                            Statistics.Received++;
                            IndeterminateCount = 0;

                            // Check for status change.
                            if (Status == ProbeStatus.Down)
                            {
                                TriggerStatusChange(new StatusChangeLog { Timestamp = DateTime.Now, Hostname = Hostname, Alias = Alias, Status = ProbeStatus.Up });
                                if (ApplicationOptions.IsEmailAlertEnabled)
                                    Util.SendEmail("up", Hostname, Alias);
                            }

                            Status = ProbeStatus.Up;
                        }
                        // No reply received.
                        else
                        {
                            Statistics.Lost++;
                            Statistics.Rate = (float)Math.Round((float)Statistics.Lost * 100 / (float)Statistics.Sent, 2);
                            IndeterminateCount++;
                            if (Status == ProbeStatus.Up) Status = ProbeStatus.Indeterminate;
                            if (Status == ProbeStatus.Inactive) Status = ProbeStatus.Down;

                            // Check for status change.
                            if (Status == ProbeStatus.Indeterminate && IndeterminateCount >= ApplicationOptions.AlertThreshold)
                            {
                                Status = ProbeStatus.Down;
                                TriggerStatusChange(new StatusChangeLog { Timestamp = DateTime.Now, Hostname = Hostname, Alias = Alias, Status = ProbeStatus.Down });
                                if (ApplicationOptions.IsEmailAlertEnabled)
                                    Util.SendEmail("down", Hostname, Alias);
                            }
                        }

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            // Update output.
                            DisplayStatistics();
                            DisplayIcmpReply(reply, Statistics);

                            // Pause between probes.
                            await IcmpWait(reply.Status);
                        }
                    }

                    catch (Exception ex)
                    {
                        Statistics.Lost++;
                        Statistics.Rate = (float)Math.Round((float)Statistics.Lost * 100 / (float)Statistics.Sent, 2);

                        // Check for status change.
                        if (Status == ProbeStatus.Inactive) Status = ProbeStatus.Down;
                        if (Status != ProbeStatus.Down)
                        {
                            Status = ProbeStatus.Down;
                            TriggerStatusChange(new StatusChangeLog { Timestamp = DateTime.Now, Hostname = Hostname, Alias = Alias, Status = ProbeStatus.Down });
                            if (ApplicationOptions.IsEmailAlertEnabled)
                                Util.SendEmail("error", Hostname, Alias);
                        }

                        // Update output.
                        DisplayIcmpReply(null, Statistics, ex);
                        DisplayStatistics();

                        // Pause between probes.
                        await Task.Delay(ApplicationOptions.PingInterval);
                    }
                }
            }
        }


        private async Task IcmpWait(IPStatus ipStatus)
        {
            if (ipStatus == IPStatus.TimedOut)
            {
                // Ping timed out.  If the ping interval is greater than the timeout,
                // then sleep for [INTERVAL - TIMEOUT]
                // Otherwise, sleep for a fixed amount of 1 second
                if (ApplicationOptions.PingInterval > ApplicationOptions.PingTimeout)
                    await Task.Delay(ApplicationOptions.PingInterval - ApplicationOptions.PingTimeout);
                else
                    await Task.Delay(1000);
            }
            else
                // For any other type of ping response, sleep for the global ping interval amount
                // before sending another ping.
                await Task.Delay(ApplicationOptions.PingInterval);
        }


        private void DisplayIcmpReply(PingReply pingReply, PingStatistics Statistics, Exception ex = null)
        {
            if (pingReply == null && ex == null) return;

            // Build output string based on the ping reply details.
            var pingOutput = new StringBuilder($"[{DateTime.Now.ToLongTimeString()}] ");

            if (pingReply != null)
            {
                switch (pingReply.Status)
                {
                    case IPStatus.Success:
                        pingOutput.Append("Reply from ");
                        pingOutput.Append(pingReply.Address.ToString());
                        if (Statistics.Min > pingReply.RoundtripTime) {
                          Statistics.Min = (uint)pingReply.RoundtripTime;
                        }
                        if (Statistics.Max < pingReply.RoundtripTime) {
                          Statistics.Max = (uint)pingReply.RoundtripTime;
                        }
                        Statistics.Average = (Statistics.Average * (Statistics.Sent - 1) + pingReply.RoundtripTime) / Statistics.Sent;
                        pingOutput.Append(": time=");
                        // if (pingReply.RoundtripTime < 1)
                        //     pingOutput.Append("  [<1ms]");
                        // else
                        pingOutput.Append($"{pingReply.RoundtripTime}ms");
                        pingOutput.Append(" seq=");
                        pingOutput.Append($"{Statistics.Sent}");
                        pingOutput.Append(" ttl=");
                        pingOutput.Append($"{pingReply.Options.Ttl}");
                        pingOutput.Append(" byte=");
                        pingOutput.Append($"{pingReply.Buffer.Length-1}");
                        break;
                    case IPStatus.DestinationHostUnreachable:
                        pingOutput.Append("Reply  [Host unreachable]");
                        break;
                    case IPStatus.DestinationNetworkUnreachable:
                        pingOutput.Append("Reply  [Network unreachable]");
                        break;
                    case IPStatus.DestinationUnreachable:
                        pingOutput.Append("Reply  [Unreachable]");
                        break;
                    case IPStatus.TimedOut:
                        pingOutput.Append("Request timed out.");
                        break;
                    default:
                        pingOutput.Append(pingReply.Status.ToString());
                        break;
                }
            }
            else
            {
                if (ex.InnerException is SocketException)
                    pingOutput.Append("Unable to resolve hostname.");
                else
                    pingOutput.Append(ex.Message);
            }

            // Add response to the output window..
            Application.Current.Dispatcher.BeginInvoke(
                new Action(() => AddHistory(pingOutput.ToString())));

            // If enabled, log output.
            WriteToLog(pingOutput.ToString());
        }
    }
}
