﻿using IXICore;
using IXICore.Meta;
using IXICore.Network;
using LW.Meta;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace LW.Network
{
    public class ProtocolMessage
    {
        public static ProtocolMessageCode waitingFor = 0;
        public static bool blocked = false;

        public static void setWaitFor(ProtocolMessageCode value)
        {
            waitingFor = value;
            blocked = true;
        }

        public static void wait(int timeout_seconds)
        {
            DateTime start = DateTime.UtcNow;
            while(blocked)
            {
                if((DateTime.UtcNow - start).TotalSeconds > timeout_seconds)
                {
                    Logging.warn("Timeout occured while waiting for " + waitingFor);
                    break;
                }
                Thread.Sleep(250);
            }
        }

        // Unified protocol message parsing
        public static void parseProtocolMessage(ProtocolMessageCode code, byte[] data, RemoteEndpoint endpoint)
        {
            if (endpoint == null)
            {
                Logging.error("Endpoint was null. parseProtocolMessage");
                return;
            }
            try
            {
                switch (code)
                {
                    case ProtocolMessageCode.hello:
                        using (MemoryStream m = new MemoryStream(data))
                        {
                            using (BinaryReader reader = new BinaryReader(m))
                            {
                                if (CoreProtocolMessage.processHelloMessage(endpoint, reader))
                                {
                                    byte[] challenge_response = null;

                                    int challenge_len = reader.ReadInt32();
                                    byte[] challenge = reader.ReadBytes(challenge_len);

                                    challenge_response = CryptoManager.lib.getSignature(challenge, Node.walletStorage.getPrimaryPrivateKey());

                                    CoreProtocolMessage.sendHelloMessage(endpoint, true, challenge_response);
                                    endpoint.helloReceived = true;
                                    return;
                                }
                            }
                        }
                        break;


                    case ProtocolMessageCode.helloData:
                        using (MemoryStream m = new MemoryStream(data))
                        {
                            using (BinaryReader reader = new BinaryReader(m))
                            {
                                if (CoreProtocolMessage.processHelloMessage(endpoint, reader))
                                {
                                    char node_type = endpoint.presenceAddress.type;
                                    if (node_type != 'M' && node_type != 'H')
                                    {
                                        CoreProtocolMessage.sendBye(endpoint, ProtocolByeCode.expectingMaster, string.Format("Expecting master node."), "", true);
                                        return;
                                    }

                                    ulong last_block_num = reader.ReadUInt64();

                                    int bcLen = reader.ReadInt32();
                                    byte[] block_checksum = reader.ReadBytes(bcLen);

                                    int wsLen = reader.ReadInt32();
                                    byte[] walletstate_checksum = reader.ReadBytes(wsLen);

                                    int consensus = reader.ReadInt32();

                                    endpoint.blockHeight = last_block_num;

                                    int block_version = reader.ReadInt32();

                                    // Check for legacy level
                                    ulong legacy_level = reader.ReadUInt64(); // deprecated

                                    int challenge_response_len = reader.ReadInt32();
                                    byte[] challenge_response = reader.ReadBytes(challenge_response_len);
                                    if (!CryptoManager.lib.verifySignature(endpoint.challenge, endpoint.serverPubKey, challenge_response))
                                    {
                                        CoreProtocolMessage.sendBye(endpoint, ProtocolByeCode.authFailed, string.Format("Invalid challenge response."), "", true);
                                        return;
                                    }

                                    // Process the hello data
                                    endpoint.helloReceived = true;
                                    NetworkClientManager.recalculateLocalTimeDifference();

                                    if (endpoint.presenceAddress.type == 'M')
                                    {
                                        Node.setNetworkBlock(last_block_num, block_checksum, block_version);

                                        // Get random presences
                                        endpoint.sendData(ProtocolMessageCode.getRandomPresences, new byte[1] { (byte)'M' });

                                        CoreProtocolMessage.subscribeToEvents(endpoint);
                                    }
                                }
                            }
                        }
                        break;

                        case ProtocolMessageCode.balance:
                        {
                            using (MemoryStream m = new MemoryStream(data))
                            {
                                using (BinaryReader reader = new BinaryReader(m))
                                {
                                    int address_length = reader.ReadInt32();
                                    byte[] address = reader.ReadBytes(address_length);

                                    // Retrieve the latest balance
                                    IxiNumber balance = reader.ReadString();

                                    if (address.SequenceEqual(Node.walletStorage.getPrimaryAddress()))
                                    {
                                        // Retrieve the blockheight for the balance
                                        ulong block_height = reader.ReadUInt64();

                                        if (block_height > Node.balance.blockHeight && (Node.balance.balance != balance || Node.balance.blockHeight == 0))
                                        {
                                            byte[] block_checksum = reader.ReadBytes(reader.ReadInt32());

                                            Node.balance.address = address;
                                            Node.balance.balance = balance;
                                            Node.balance.blockHeight = block_height;
                                            Node.balance.blockChecksum = block_checksum;
                                            Node.balance.verified = false;
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case ProtocolMessageCode.updatePresence:
                        {
                            // Parse the data and update entries in the presence list
                            PresenceList.updateFromBytes(data);
                        }
                        break;

                    case ProtocolMessageCode.blockHeaders:
                        {
                            // Forward the block headers to the TIV handler
                            Node.tiv.receivedBlockHeaders(data, endpoint);
                        }
                        break;

                    case ProtocolMessageCode.pitData:
                        {
                            Node.tiv.receivedPIT(data, endpoint);
                        }
                        break;

                    case ProtocolMessageCode.newTransaction:
                    case ProtocolMessageCode.transactionData:
                        {
                            Transaction tx = new Transaction(data, true);

                            PendingTransactions.increaseReceivedCount(tx.id);

                            Node.tiv.receivedNewTransaction(tx);
                            Console.WriteLine("Received new transaction {0}", tx.id);
                        }
                        break;

                    default:
                        break;

                }
            }
            catch (Exception e)
            {
                Logging.error(string.Format("Error parsing network message. Details: {0}", e.ToString()));
            }

            if(waitingFor == code)
            {
                blocked = false;
            }
        }

    }
}
