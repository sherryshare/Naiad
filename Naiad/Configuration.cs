/*
 * Naiad ver. 0.2
 * Copyright (c) Microsoft Corporation
 * All rights reserved. 
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0 
 *
 * THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR
 * CONDITIONS OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT
 * LIMITATION ANY IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR
 * A PARTICULAR PURPOSE, MERCHANTABLITY OR NON-INFRINGEMENT.
 *
 * See the Apache Version 2.0 License for specific language governing
 * permissions and limitations under the License.
 */

using System;

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

using System.Threading;
using Naiad.DataStructures;
using Naiad.Dataflow.Channels;
using Naiad.CodeGeneration;
using Naiad.Frameworks;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net;
using Naiad.Util;
using Naiad.Scheduling;
using Naiad.Runtime.Controlling;
using Naiad.FaultTolerance;
using Naiad.Runtime.Networking;
using Naiad.Runtime.Progress;

namespace Naiad
{

    public enum RuntimeStatistic
    {
        TxProgressMessages, TxProgressBytes,
        RxProgressMessages, RxProgressBytes,
        ProgressLocalRecords,
        TxHighPriorityMessages, TxHighPriorityBytes,
        TxNormalPriorityMessages, TxNormalPriorityBytes,
        RxNetMessages, RxNetBytes,
        NUM_STATISTICS
    }

    public class Configuration
    {
        private static Configuration staticConfiguration = null;
        /// <summary>
        /// Hack to expose a configuration to static components (i.e. code generation).
        /// </summary>
        internal static Configuration StaticConfiguration
        {
            get
            {
                if (staticConfiguration == null)
                {
                    string[] args = new string[0];
                    staticConfiguration = Configuration.FromArgs(ref args);
                }
                return staticConfiguration;
            }
            set
            {
                staticConfiguration = value;
            }
        }

        public enum BroadcastProtocol
        {
            TcpOnly,
            UdpOnly,
            TcpUdp
        }

        public enum SendBufferMode
        {
            Global,
            PerRemoteProcess,
            PerWorker
        }

        public static Configuration Default
        {
            get
            {
                string[] emptyArgs = new string[0];
                Configuration ret = Configuration.FromArgs(ref emptyArgs);
                return ret;
            }
        }

        private SendBufferMode sendBufferPolicy = SendBufferMode.PerRemoteProcess;
        public SendBufferMode SendBufferPolicy
        {
            get { return this.sendBufferPolicy; }
            set { this.sendBufferPolicy = value; }
        }

        private int sendPageCount = 1 << 16;
        public int SendPageCount
        {
            get { return this.sendPageCount; }
            set { this.sendPageCount = value; }
        }

        private int sendPageSize = 1 << 14;
        public int SendPageSize
        {
            get { return this.sendPageSize; }
            set { this.sendPageSize = value; }
        }

        private int processID = 0;
        /// <summary>
        /// The ID of the local process in this computation (starting at 0).
        /// </summary>
        public int ProcessID { get { return this.processID; } set { this.processID = value; } }

        /// <summary>
        /// The number of processes in this computation.
        /// </summary>
        public int Processes { get { return this.endpoints != null ? this.endpoints.Length : 1; } }

        private int workerCount = 1;
        /// <summary>
        /// The number of workers (i.e. CPUs used) in the local process.
        /// 
        /// N.B. At present, all processes in a computation must use the same worker count.
        /// </summary>
        public int WorkerCount { get { return this.workerCount; } set { this.workerCount = value; } }

        private int replication = 0;
        /// <summary>
        /// The number of secondary copies of each stage to maintain. The default value of zero indicates no
        /// replication.
        /// </summary>
        public int Replication { get { return this.replication; } set { this.replication = value; } }

        private IPEndPoint[] endpoints = null;
        /// <summary>
        /// The network addresses of the processes in this computation, indexed by process ID.
        /// </summary>
        public IPEndPoint[] Endpoints { get { return this.endpoints == null ? null : this.endpoints.ToArray(); } set { this.endpoints = value.ToArray(); } }

        private int reachabilityInterval = 1000;
        /// <summary>
        /// The periodic interval in milliseconds at which Naiad will attempt to compact its collection state.
        /// </summary>
        public int CompactionInterval { get { return this.reachabilityInterval; } set { this.reachabilityInterval = value; } }

        private int deadlockTimeout = Timeout.Infinite;
        /// <summary>
        /// Setting this to a value other than Timeout.Infinite will cause Naiad to print diagnostic information
        /// after a process sleeps for that number of milliseconds.
        /// </summary>
        public int DeadlockTimeout { get { return this.deadlockTimeout; } set { this.deadlockTimeout = value; } }

        private bool multipleLocalProcesses = true;
        /// <summary>
        /// Setting this to true ensures that, if cores are pinned to CPUs, subsequent processes are pinned to different
        /// sets of CPUs.
        /// </summary>
        public bool MultipleLocalProcesses { get { return this.multipleLocalProcesses; } set { this.multipleLocalProcesses = value; } }

        private int centralizerProcessId = 0;
        public int CentralizerProcessId { get { return this.centralizerProcessId; } set { this.centralizerProcessId = value; } }

        private int centralizerThreadId = 0;
        public int CentralizerThreadId { get { return this.centralizerThreadId; } set { this.centralizerThreadId = value; } }

        private bool distributedProgressTracker = false;
        /// <summary>
        /// EXPERIMENTAL: Setting this to true enables the (original) distributed progress tracker.  Default is not.
        /// </summary>
        public bool DistributedProgressTracker { get { return this.distributedProgressTracker; } set { this.distributedProgressTracker = value; } }

        private bool impersonation = false;
        public bool Impersonation { get { return this.impersonation; } set { this.impersonation = value; } }

        private bool duplexSockets = false;
        /// <summary>
        /// If true, uses the same socket for sending and receiving data.
        /// </summary>
        public bool DuplexSockets { get { return this.duplexSockets; } set { this.duplexSockets = value; } }

        private bool nagling = false;
        /// <summary>
        /// If true, uses Nagling on sockets
        /// </summary>
        public bool Nagling { get { return this.nagling; } set { this.nagling = value; } }

        private bool keepalives = false;
        /// <summary>
        /// If true, enable TCP KeepAlives on sockets
        /// </summary>
        public bool KeepAlives { get { return this.keepalives; } set { this.keepalives = value; } }

        private bool nothighpriorityqueue = false;
        /// <summary>
        /// If true, don't use the high priority queue for progress traffic
        /// </summary>
        public bool DontUseHighPriorityQueue { get { return this.nothighpriorityqueue; } set { this.nothighpriorityqueue = value; } }
        
        private bool domainReporting = false;
        /// <summary>
        /// Enables a reporting graph manager. In the future this is intended to be used for logging messages that
        /// are aggregated according to real time, though for now it is mostly vestigial. Calling Log(string) on the
        /// reporter at a vertex will only deliver the messages to a central location if domain reporting is enabled.
        /// Messages are all written to a file called rtdomain.txt at the root vertex's computer.
        /// </summary>
        public bool DomainReporting { get { return this.domainReporting; } set { this.domainReporting = value; } }

        private bool inlineReporting = false;
        /// <summary>
        /// Enables inline reporting in the user graph domain. This is used for logging messages that are aggregated
        /// according to logical times, e.g. to discover the number of messages written by a stage during a particular
        /// loop iteration, or epoch. Calling Log(string,time) on the reporter at a vertex will only deliver the messages
        /// to a central location if domain reporting is enabled. Messages are all written to a file called rtinline.txt
        /// at the root vertex's computer.
        /// </summary>
        public bool InlineReporting { get { return this.inlineReporting; } set { this.inlineReporting = value; } }

        private bool aggregateReporting = false;
        /// <summary>
        /// Enables aggregation in inline reporting in the graph manager domain. Calling LogAggregate(...) on the reporter at a
        /// vertex will only aggregate and log messages if both InlineReporting and AggregateReporting are true. Aggregates
        /// are written to the rtinline.txt file at the root vertex's computer.
        /// </summary>
        public bool AggregateReporting { get { return this.aggregateReporting; } set { this.aggregateReporting = value; } }

        private bool collectNetStats = false;
        /// <summary>
        /// Periodically prints network interface and TCP statistics to a file.
        /// </summary>
        public bool CollectNetStats { get { return this.collectNetStats; } set { this.collectNetStats = value; } }

        private BroadcastProtocol broadcast = BroadcastProtocol.TcpOnly;
        /// <summary>
        /// The network protocol to be used for broadcasting control messages.
        /// 
        /// N.B. Support for TcpUdp and UdpOnly is experimental. The BroadcastAddress must be set to use these modes.
        /// </summary>
        public BroadcastProtocol Broadcast { get { return this.broadcast; } set { this.broadcast = value; } }

        private bool variableLengthSerialization = false;
        public bool VariableLengthSerialization { get { return this.variableLengthSerialization; } set { this.variableLengthSerialization = value; } }

        private bool oneTimePerMessageSerialization = true;
        public bool OneTimePerMessageSerialization { get { return this.oneTimePerMessageSerialization; } set { this.oneTimePerMessageSerialization = value; } }

        private IPEndPoint broadcastAddress = null;
        /// <summary>
        /// The address and port to be used for sending or receiving broadcast control messages.
        /// 
        /// N.B. Support for broadcast primitives is experimental. This must be set to use the TcpUdp and UdpOnly broadcast protocols.
        /// </summary>
        public IPEndPoint BroadcastAddress { get { return this.broadcastAddress; } set { this.broadcastAddress = value; } }

        private string interfaceName = "Ethernet 2";
        public string NetStatsInterfaceName { get { return this.interfaceName; } set { this.interfaceName = value; } }

        private bool useBroadcastWakeup = false;
        /// <summary>
        /// Uses optimization to wake multiple threads with a single kernel event.
        /// </summary>
        public bool UseBroadcastWakeup { get { return this.useBroadcastWakeup; } set { this.useBroadcastWakeup = value; } }

        private bool useNetworkBroadcastWakeup = false;
        /// <summary>
        /// Uses optimization to wake multiple networking threads with a single kernel event.
        /// </summary>
        public bool UseNetworkBroadcastWakeup { get { return this.useNetworkBroadcastWakeup; } set { this.useNetworkBroadcastWakeup = value; } }

        /// <summary>
        /// Prints information about the standard Naiad command-line options, which are used to build
        /// a configuration in Configuration.FromArgs().
        /// </summary>
        public static void Usage()
        {
            Console.Error.WriteLine("Naiad options:\n"
                + String.Format("\t--procid,-p\tProcess ID (default 0)\n")
                + String.Format("\t--threads,-t\tNumber of threads (default 1)\n")
                + String.Format("\t--numprocs,-n\tNumber of processes (default 1)\n")
                + "\t--hosts,-h\tList of hostnames in the form host:port\n"
                + "\t--local\t\tShorthand for -h localhost:2101 localhost:2102 ... localhost:2100+numprocs\n"
                //+ String.Format("\t--log-level,-l\tDebug|Info|Progress|Error|Fatal|Off (default Progress)\n")
                //+ String.Format("\t--log-type\tConsole|File|Memory (default Console)\n")
                //+ String.Format("\t--reachability\tmilliseconds per reachability (default {0})\n", reachabilityInterval)
                //+ String.Format("\t--deadlocktimeout\tmilliseconds before exit if deadlocked (default {0})\n", deadlockTimeout)
                );
            Console.Error.WriteLine("Runs single-process by default, optionally set -t for number of threads");
            Console.Error.WriteLine("To run multiprocess, specify -p, -n, and -h");
            Console.Error.WriteLine("   Example for 2 machines, M1 and M2, using TCP port 2101:");
            Console.Error.WriteLine("      M1> NaiadExamples.exe pagerank -p 0 -n 2 -h M1:2101 M2:2101");
            Console.Error.WriteLine("      M2> NaiadExamples.exe pagerank -p 1 -n 2 -h M1:2101 M2:2101");
            Console.Error.WriteLine("To run multiprocess on one machine, specify -p, -n and --local for each process");
        }

        /// <summary>
        /// Builds a Naiad configuration from the given command-line arguments, interpreting them according to
        /// the output of Configuration.Usage().
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        public static Configuration FromArgs(ref string[] args)
        {
            var config = new Configuration();

            List<string> strippedArgs = new List<string>();

            // Defaults
            string[] Prefixes = new string[] { "localhost:2101" };

            bool procIDSet = false;
            bool numProcsSet = false;
            bool hostsSet = false;
            
            bool multipleProcsSingleMachine = true;

            int Processes = 1;

            //Flags.Define("-l,--log-level", LoggingLevel.Progress);
            //Flags.Define("--log-type", LoggingStyle.Console);
            //args = Flags.Parse(args);
            //Logging.LogLevel = Flags.Get("log-level").EnumValue<LoggingLevel>();
            //Logging.LogStyle = Flags.Get("log-type").EnumValue<LoggingStyle>();
            
            Logging.Init();
#if TRACING_ON
            Console.Error.WriteLine("Tracing enabled (recompile without TRACING_ON to disable)");
#else
            //Console.Error.WriteLine("Tracing disabled (recompile with TRACING_ON to enable)");
#endif
            
            int i = 0;
            while (i < args.Length)
            {
                switch (args[i])
                {
                    case "--usage":
                    case "--help":
                    case "-?":
                        Usage();
                        ++i;
                        break;
                    case "--procid":
                    case "-p":
                        config.ProcessID = Int32.Parse(args[i + 1]);
                        i += 2;
                        procIDSet = true;
                        break;
                    case "--threads":
                    case "-t":
                        config.WorkerCount = Int32.Parse(args[i + 1]);
                        i += 2;
                        break;
                    case "--varlengthint":
                        config.VariableLengthSerialization = true;
                        ++i;
                        break;
                    case "--basic":
                        config.OneTimePerMessageSerialization = false;
                        ++i;
                        break;
                    case "--numprocs":
                    case "-n":
                        Processes = Int32.Parse(args[i + 1]);
                        i += 2;
                        numProcsSet = true;
                        break;
                    case "--hosts":
                    case "-h":
                        if (!numProcsSet)
                        {
                            Logging.Error("Error: --numprocs must be specified before --hosts");
                            Usage();
                            System.Environment.Exit(-1);
                        }
                        if (hostsSet)
                        {
                            Logging.Error("Error: --local cannot be combined with --hosts");
                            Usage();
                            System.Environment.Exit(-1);
                        }
                        Prefixes = new string[Processes];
                        if (args[i + 1].StartsWith("@"))
                        {
                            string hostsFilename = args[i + 1].Substring(1);
                            try
                            {
                                using (StreamReader reader = File.OpenText(hostsFilename))
                                {
                                    for (int j = 0; j < Processes; ++j)
                                    {
                                        if (reader.EndOfStream)
                                        {
                                            Logging.Error("Error: insufficient number of hosts in the given hosts file, {0}", hostsFilename);
                                            System.Environment.Exit(-1);
                                        }
                                        Prefixes[j] = reader.ReadLine().Trim();
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                Logging.Error("Error: cannot read hosts file, {0}", hostsFilename);
                                System.Environment.Exit(-1);
                            }
                            i += 2;
                        }
                        else
                        {
                            Array.Copy(args, i + 1, Prefixes, 0, Processes);
                            i += 1 + Processes;
                        }
                        hostsSet = true;
                        break;
                    case "--local":
                        if (!numProcsSet)
                        {
                            Logging.Error("Error: --numprocs must be specified before --local");
                            Usage();
                            System.Environment.Exit(-1);
                        }
                        if (hostsSet)
                        {
                            Logging.Error("Error: --local cannot be combined with --hosts");
                            Usage();
                            System.Environment.Exit(-1);
                        }
                        Prefixes = Enumerable.Range(2101, Processes).Select(x => string.Format("localhost:{0}", x)).ToArray();
                        i++;
                        hostsSet = true;
                        break;
                    case "--reachability":
                        config.CompactionInterval = Convert.ToInt32(args[i + 1]);
                        i += 2;
                        break;
                    case "--deadlocktimeout":
                        config.DeadlockTimeout = int.Parse(args[i + 1]);
                        i += 2;
                        break;
                    case "--distributedprogresstracker":
                        config.DistributedProgressTracker = true;
                        i++;
                        break;
                    case "--impersonation":
                        config.Impersonation = true;
                        i++;
                        break;
                    case "--duplex":
                        config.DuplexSockets = true;
                        i++;
                        break;
                    case "--nagling":
                        config.Nagling = true;
                        i++;
                        break;
                    case "--keepalives":
                        config.KeepAlives = true;
                        Logging.Progress("TCP keep-alives enabled");
                        i++;
                        break;
                    case "--nothighpriorityqueue":
                        config.DontUseHighPriorityQueue = true;
                        i++;
                        break;
                    case "--domainreporting":
                        config.DomainReporting = true;
                        i++;
                        break;
                    case "--inlinereporting":
                        config.InlineReporting = true;
                        i++;
                        break;
                    case "--aggregatereporting":
                        config.AggregateReporting = true;
                        i++;
                        break;
                    case "--netstats":
                        config.CollectNetStats = true;
                        i++;
                        break;
                    case "--netstatsinterface":
                        // doesn't handle multiword names, eg "Ethernet 2"
                        // You'll have to hard-code if you want that
                        config.interfaceName = args[i+1];
                        i += 2;
                        break;
                    case "--broadcastprotocol":
                        switch (args[i + 1].ToLower())
                        {
                            case "tcp":
                            case "tcponly":
                                config.Broadcast = BroadcastProtocol.TcpOnly;
                                break;
                            case "udp":
                            case "udponly":
                                config.Broadcast = BroadcastProtocol.UdpOnly;
                                break;
                            case "tcpudp":
                            case "udptcp":
                            case "both":
                                config.Broadcast = BroadcastProtocol.TcpUdp;
                                break;
                            default:
                                Logging.Error("Warning: unknown broadcast protocol ({0})", args[i + 1]);
                                Logging.Error("Valid broadcast protocols are: tcp, udp, both");
                                config.Broadcast = BroadcastProtocol.TcpOnly;
                                break;
                        }
                        i += 2;
                        break;
                    case "--broadcastaddress":
                        {
                            string broadcastHostname;
                            int broadcastPort = 2101;
                            bool success = ParseName(args[i + 1], out broadcastHostname, ref broadcastPort);
                            if (!success)
                            {
                                Logging.Error("Error: cannot set broadcast address to {0}", args[i + 1]);
                                System.Environment.Exit(-1);
                            }
                            config.BroadcastAddress = new IPEndPoint(GetIPAddressForHostname(broadcastHostname), broadcastPort);
                        }
                        i += 2;
                        break;
                    case "--broadcastwakeup":
                        {
                            config.UseBroadcastWakeup = true;
                            Console.Error.WriteLine("Using broadcast wakeup");
                            i++;
                            break;
                        }
                    case "--netbroadcastwakeup":
                        {
                            config.UseNetworkBroadcastWakeup = true;
                            Console.Error.WriteLine("Using broadcast wakeup for networking threads");
                            i++;
                            break;
                        }
                    case "--sendpool":
                        {
                            switch (args[i + 1].ToLower())
                            {
                                case "global":
                                    config.SendBufferPolicy = SendBufferMode.Global;
                                    break;
                                case "process":
                                    config.SendBufferPolicy = SendBufferMode.PerRemoteProcess;
                                    break;
                                case "worker":
                                    config.SendBufferPolicy = SendBufferMode.PerWorker;
                                    break;
                                default:
                                    Logging.Error("Warning: send buffer policy ({0})", args[i + 1]);
                                    Logging.Error("Valid broadcast protocols are: global, process, worker");
                                    config.SendBufferPolicy = SendBufferMode.PerRemoteProcess;
                                    break;
                            }
                            i += 2;
                            break;
                        }
                    case "--sendpagesize":
                        {
                            config.SendPageSize = int.Parse(args[i + 1]);
                            i += 2;
                            break;
                        }
                    case "--sendpagecount":
                        {
                            config.SendPageCount = int.Parse(args[i + 1]);
                            i += 2;
                            break;
                        }
                    default:
                        strippedArgs.Add(args[i]);
                        i++;
                        break;
                }
            }

            if (procIDSet && !numProcsSet)
            {
                Logging.Error("Error: Number of processes not supplied (use the --numprocs option, followed by an integer)");
                Usage();
                System.Environment.Exit(-1);
            }

            if (numProcsSet && Processes > 1 && !hostsSet)
            {
                Logging.Error("Error: List of hosts not supplied (use the --hosts option, followed by {0} hostnames)", Processes);
                System.Environment.Exit(-1);
            }

            if (config.Broadcast != BroadcastProtocol.TcpOnly && config.BroadcastAddress == null)
            {
                Logging.Error("Error: Must set --broadcastaddress to use non-TCP broadcast protocol");
                System.Environment.Exit(-1);
            }

            var names = Prefixes.Select(x => x.Split(':')).ToArray();

            IPEndPoint[] endpoints = new IPEndPoint[Processes];

            // Check whether we have multiple processes on the same machine (used for affinitizing purposes)
            string prevHostName = Prefixes[0].Split(':')[0];

            for (int j = 0; j < endpoints.Length; ++j)
            {
                string hostname;
                int port = 2101;
                bool success = ParseName(Prefixes[j], out hostname, ref port);
                if (!success)
                    System.Environment.Exit(-1);
                if (hostname != prevHostName)
                    multipleProcsSingleMachine = false;
                else prevHostName = hostname;

                IPAddress ipv4Address = GetIPAddressForHostname(hostname);

                try
                {
                    endpoints[j] = new System.Net.IPEndPoint(ipv4Address, port);
                }
                catch (ArgumentOutOfRangeException)
                {
                    Logging.Error("Error: invalid port ({0}) for process {1}", port, j);
                    System.Environment.Exit(-1);
                }
            }
            config.Endpoints = endpoints;

            args = strippedArgs.ToArray();

            // Used to set scheduler thread affinity
            config.MultipleLocalProcesses = multipleProcsSingleMachine;

            return config;
        }

        private static bool ParseName(string prefix, out string hostname, ref int port)
        {

            string[] splitName = prefix.Split(':');
            hostname = splitName[0];

            if (splitName.Length == 2 && !int.TryParse(splitName[1], out port))
            {
                {
                    Logging.Error("Error: invalid port number ({0})", splitName[1]);
                    return false;
                }
            }

            return true;
        }

        private static bool IsAutoConf(IPAddress addr)
        {
            byte[] bytes = addr.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            return false;
        }

        private static IPAddress GetIPAddressForHostname(string hostname)
        {
            IPAddress[] ipAddresses = null;
            try
            {
                ipAddresses = Dns.GetHostAddresses(hostname);
            }
            catch (Exception)
            {
                Logging.Error("Error: could not resolve hostname {0}", hostname);
                System.Environment.Exit(-1);
            }

            IPAddress ipv4Address;
            ipv4Address = ipAddresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IsAutoConf(x));
            if (ipv4Address == null)
            {
                Logging.Error("Error: could not find an IPv4 address for hostname {0}", hostname);
                Logging.Error("IPv6 is not currently supported.");
                System.Environment.Exit(-1);
            }

            return ipv4Address;
        }
    }

    public interface WorkerGroup
    {
        int Count { get; }

        /// <summary>
        /// This event is fired by each worker when it initially starts.
        /// </summary>
        event EventHandler<SchedulerStartArgs> Starting;
        /// <summary>
        /// This event is fired by each worker when it wakes from sleeping.
        /// </summary>
        event EventHandler<SchedulerWakeArgs> Waking;
        /// <summary>
        /// This event is fired by a worker immediately before executing a work item.
        /// </summary>
        event EventHandler<OperatorStartArgs> WorkItemStarting; 
        /// <summary>
        /// This event is fired by a worker immediately after executing a work item.
        /// </summary>
        event EventHandler<OperatorEndArgs> WorkItemEnding;
        /// <summary>
        /// This event is fired by a worker immediately after enqueueing a work item.
        /// </summary>
        event EventHandler<OperatorEnqArgs> WorkItemEnqueued;
        /// <summary>
        /// This event is fired by a worker when it becomes idle, because it has no work to execute.
        /// </summary>
        event EventHandler<SchedulerSleepArgs> Sleeping;
        /// <summary>
        /// This event is fired by a worker when it has finished all work, and the computation has terminated.
        /// </summary>
        event EventHandler<SchedulerTerminateArgs> Terminating;
        /// <summary>
        /// This event is fired by a worker when a batch of records is delivered to an operator.
        /// </summary>
        event EventHandler<OperatorReceiveArgs> ReceivedRecords;
        /// <summary>
        /// This event is fired by a worker when a batch of records is sent by an operator.
        /// (N.B. This event is currently not used.)
        /// </summary>
        event EventHandler<OperatorSendArgs> SentRecords;
    }
}
