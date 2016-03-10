using System;
using System.IO;
using System.Threading;
using System.Text;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;

public class DisqueConnectionController : IDisposable {
	private readonly string disqueId;
	
	private ServerContext context;

	/*
		redis connection settings
	*/
	const string host = "127.0.0.1";
	const int port = 7711;
	const int timeout = -1;


	// disque commands
	const string DISQUE_COMMAND_ADDJOB	= "ADDJOB";
	const string DISQUE_COMMAND_GETJOB	= "GETJOB";
	const string DISQUE_COMMAND_FASTACK	= "FASTACK";


	// outputter
	private DisqueConnectionSocket addStream;

	// receiver
	private DisqueConnectionSocket getStream;

	// acknowledge
	private DisqueConnectionSocket ackStream;


	private readonly string[] getJobOptions;
	private readonly string[] addJobOptions;

	// header of data.
	private const char HEADER_STRING	= 's';
	private const char HEADER_BINARY	= 'b';
	private const char HEADER_CONTROL	= 'c';

	// webSocket server state for each connection. syncronized to nginx-lua client.lua code.
	private const char STATE_CONNECT			= '1';
	private const char STATE_STRING_MESSAGE		= '2';
	private const char STATE_BINARY_MESSAGE		= '3';
	private const char STATE_DISCONNECT_INTENT	= '4';
	private const char STATE_DISCONNECT_ACCIDT	= '5';
	private const char STATE_DISCONNECT_DISQUE_ACKFAILED = '6';
	private const char STATE_DISCONNECT_DISQUE_ACCIDT_SENDFAILED = '7';


	private const int CONNECTION_ID_LEN = 36;// 65D76DEE-0E68-424E-A18F-6D2CC9656FB3

	private readonly DisqueDataMode dataMode;
	public enum DisqueDataMode {
		disque_binary,
		disque_string
	};

	public DisqueConnectionController (Action<string, Func<bool>> Updater, string contextQueueIdentity, string dataMode) {
		disqueId = Guid.NewGuid().ToString();
		
		// only from client-connection to this context queue name is predefined. connection queue names are not defined in code. these are connection-id of connection.
		this.getJobOptions = new string[]{"count", "1000", "from", contextQueueIdentity};
		this.addJobOptions = new string[]{"0"};

		this.dataMode = (DisqueDataMode)Enum.Parse(typeof(DisqueDataMode), dataMode);

		// start addjob-connection
		addStream = new DisqueConnectionSocket("addOnlySocket", host, port, timeout);
		
		// start getjob-connection
		getStream = new DisqueConnectionSocket("getOnlySocket", host, port, timeout);
		
		ackStream = new DisqueConnectionSocket("ackOnlySocket", host, port, timeout);


		// (Develop.TIME_ASSERT).TimeAssert("切断からの復帰を自動的に行うのを考えたら、これじゃまずい。");
		Updater("disque_" + disqueId, GetJobsOnUpdate);
	}
	

	public void SetContext (ServerContext context) {
		this.context = context;
		context.SetPublisher(Publish);
	}

	public void Publish (object messageObj, string targetConnectionId) {
		if (string.IsNullOrEmpty(targetConnectionId)) return;
		AddJob(messageObj, targetConnectionId);
	}

	/**
		DisqueへのaddJobを行う
		connectionId is queue id.
	*/
	private void AddJob (object messageObj, string targetConnectionId) {
		Func<string, int> Send = (string connectionId) => {
			if (dataMode == DisqueDataMode.disque_binary) return addStream.SendByteCommand(DISQUE_COMMAND_ADDJOB, connectionId, (byte[])messageObj, addJobOptions);
			return addStream.SendStringCommand(DISQUE_COMMAND_ADDJOB, connectionId, messageObj.ToString(), addJobOptions);
		};

		/*
			send data to target queue.
			by queueId, message, options.
		*/
		
		var result = Send(targetConnectionId);
		if (0 < result) {
			
			var firstByte = addStream.ReadFirstByte();

			switch (firstByte) {
				case 43: {// '+' 
					// var id = 
					addStream.ReadLineBytes();
					// XrossPeer.Log("targetConId:" + targetConId + " addjob result:" + result + " id:" + Encoding.UTF8.GetString(id));
					break;
				}
				case 45: {// '-' error
					var errorStr = Encoding.UTF8.GetString(getStream.ReadLineBytes());
					// XrossPeer.Log("errorStr:" + errorStr);
					break;
				}
				case 58: {// ':' integer
					var resultStr = Encoding.UTF8.GetString(getStream.ReadLineBytes());
					// XrossPeer.Log("int result:" + resultStr);
					break;
				}
				default: {
					// XrossPeer.Log("sending abnormal result:" + firstByte);
					break;
				}
			}
		}
	}


	/**
		受け取ったjobを解釈、contextへと入力する。APIレイヤーはserverContext側にあるので、データを扱うこの辺は変更なしで行けるはず。
	*/
	public void InputDatasToContext (List<byte[]> datas) {
		// XrossPeer.Log("datas received. datas:" + datas.Count);
		/*
			messageとして受け取ったjobを、list化して読み込む。
		*/
		foreach (var dataArray in datas) {
			var len = dataArray.Length;
			if (len < 1/*s or b or c*/ + 1/*state param*/ + CONNECTION_ID_LEN/*connectionId*/) {
				var invalidMessage = Encoding.ASCII.GetString(dataArray);
				// XrossPeer.Log("illigal format invalidMessage1:" + invalidMessage);
				continue;
			}

			var header = (char)dataArray[0];
			switch (header) {
				case HEADER_CONTROL:
				case HEADER_STRING:
				case HEADER_BINARY: {
					break;
				}
				default: {
					var invalidMessage = Encoding.ASCII.GetString(dataArray);
					// XrossPeer.Log("illigal format invalidMessage2:" + invalidMessage);
					continue;
				}
			}
			
			var state = (char)dataArray[1];

			// dataArray[2-38] is connectionId, length = definitely CONNECTION_ID_LEN.
			var connectionId = Encoding.ASCII.GetString(dataArray, 2, CONNECTION_ID_LEN);
			
			switch (state) {
				case STATE_CONNECT: {
					// XrossPeer.Log("STATE_CONNECT");
					if (2 + CONNECTION_ID_LEN < len) {
						var dataLen = len - (2 + CONNECTION_ID_LEN);
						var data = new byte[dataLen];
						Buffer.BlockCopy(dataArray, (2 + CONNECTION_ID_LEN), data, 0, dataLen);
						context.OnConnected(connectionId, data);	
					}
					break;
				}

				case STATE_STRING_MESSAGE: {
					if (2 + CONNECTION_ID_LEN < len) {
						var data = Encoding.UTF8.GetString(dataArray, 2 + CONNECTION_ID_LEN, len - (2 + CONNECTION_ID_LEN));
						context.OnMessage(connectionId, data);
					}
					break;
				}

				case STATE_BINARY_MESSAGE: {
					if (2 + CONNECTION_ID_LEN < len) {
						var dataLen = len - (2 + CONNECTION_ID_LEN);
						var data = new byte[dataLen];
						Buffer.BlockCopy(dataArray, (2 + CONNECTION_ID_LEN), data, 0, dataLen);
						context.OnMessage(connectionId, data);
					}
					break;
				}

				case STATE_DISCONNECT_INTENT: {
					if (2 + CONNECTION_ID_LEN < len) {
						var dataLen = len - (2 + CONNECTION_ID_LEN);
						var data = new byte[dataLen];
						Buffer.BlockCopy(dataArray, (2 + CONNECTION_ID_LEN), data, 0, dataLen);
						// XrossPeer.Log("client closed");
						context.OnDisconnected(connectionId, data, "intentional disconnect.");
					}
					break;
				}

				case STATE_DISCONNECT_ACCIDT: {
					if (2 + CONNECTION_ID_LEN < len) {
						var dataLen = len - (2 + CONNECTION_ID_LEN);
						var data = new byte[dataLen];
						Buffer.BlockCopy(dataArray, (2 + CONNECTION_ID_LEN), data, 0, dataLen);
						
						context.OnDisconnected(connectionId, data, "accidential disconnect.");
					}
					break;
				}
				case STATE_DISCONNECT_DISQUE_ACKFAILED: {
					if (2 + CONNECTION_ID_LEN < len) {
						var dataLen = len - (2 + CONNECTION_ID_LEN);
						var data = new byte[dataLen];
						Buffer.BlockCopy(dataArray, (2 + CONNECTION_ID_LEN), data, 0, dataLen);
						
						context.OnDisconnected(connectionId, data, "accidential disconnect.");
					}
					break;
				}
				case STATE_DISCONNECT_DISQUE_ACCIDT_SENDFAILED: {
					if (2 + CONNECTION_ID_LEN < len) {
						var dataLen = len - (2 + CONNECTION_ID_LEN);
						var data = new byte[dataLen];
						Buffer.BlockCopy(dataArray, (2 + CONNECTION_ID_LEN), data, 0, dataLen);
						
						context.OnDisconnected(connectionId, data, "send failed to client. disconnect.");
					}
					break;
				}

				default: {
					// XrossPeer.Log("undefined websocket state:" + state);
					break;
				}
			}
		}
	}


	/*
		メッセージIDとメッセージ自体を運ぶコンテナ
	*/
	List<string> receivedMessageIds = new List<string>();
	List<byte[]> receivedDatas = new List<byte[]>();

	public bool GetJobsOnUpdate () {
		while (true) {
			var retBytesLen = getStream.SendStringCommand(DISQUE_COMMAND_GETJOB, string.Empty, string.Empty, getJobOptions);
			if (retBytesLen == 0) break;
			
			// sock.Receive(buf, 0, buf.Length, SocketFlags.None);
			var firstByte = getStream.ReadFirstByte();

			// check contains error signal or not.
			switch (firstByte) {
				case 45: {// '-' error
					var errorStr = Encoding.UTF8.GetString(getStream.ReadLineBytes());
					// XrossPeer.Log("GetJobs error:" + errorStr);
					continue;
				}
				case 42: {// '*' bulk read
					
					// 1st line is data count.
					var dataCountLine = getStream.ReadLineBytes();
					var count = Encoding.UTF8.GetString(dataCountLine);
					var countNum = Convert.ToInt32(count);
					// XrossPeer.Log("countNum:" + countNum);// 複数件が入っていて、getjobのcountにNを指定しても、1 ~ N件の間を彷徨う。よくわからんが、まあ気にしないでいいや。キューだし。

					/*
						receive messages.
					*/
					for (int i = 0; i < countNum; i++) {
						// get count of this contents. ignore '*'
						getStream.ReadFirstByte();
						// var elementCountLine = 
							getStream.ReadLineBytes();
						// var elememtCount = Encoding.UTF8.GetString(elementCountLine);
						// XrossPeer.Log("elememtCount:" + elememtCount);
						
						// get queueNameLength, ignore '$'
						getStream.ReadFirstByte();
						// var queueNameLenLine = 
							getStream.ReadLineBytes();
						// var queueNameLength = Encoding.UTF8.GetString(queueNameLenLine);
						// XrossPeer.Log("queueNameLength:" + queueNameLength);

						// get queueName
						// var queueNameLine = 
							getStream.ReadLineBytes();
						// var queueName = Encoding.UTF8.GetString(queueNameLine);
						// XrossPeer.Log("queueName:" + queueName);

						// get messageIdLen, ignore '$'
						getStream.ReadFirstByte();
						// var messageIdLenLine = 
							getStream.ReadLineBytes();
						// var messageIdLen = Encoding.UTF8.GetString(messageIdLenLine);
						// XrossPeer.Log("messageIdLen:" + messageIdLen);

						// get messageId
						var messagIdLine = getStream.ReadLineBytes();
						var messageId = Encoding.UTF8.GetString(messagIdLine);
						// XrossPeer.Log("messageId:" + messageId);						
						
						// append ids to messageId list.
						receivedMessageIds.Add(messageId);
					
						// get messageLen, ignore $
						getStream.ReadFirstByte();
						var messageLenLine = 
							getStream.ReadLineBytes();
						var messageLen = Int32.Parse(Encoding.UTF8.GetString(messageLenLine));
						// XrossPeer.Log("messageLen:" + messageLen);

						// get message
						var message = getStream.ReadBytes(messageLen);
						// var message = Encoding.UTF8.GetString(messageLine);
						// XrossPeer.Log("message:" + message);

						// append messageDataArray to messageDataArray list.
						receivedDatas.Add(message);
					}
					break;
				}

				default: {
					// XrossPeer.Log("getjob unknown operator:" + firstByte + " char:" + Convert.ToChar(firstByte));
					continue;
				}
			}
		}

		// no data left in stream. proceed 
		if (receivedMessageIds.Any()) {
			// XrossPeer.Log("receivedMessageIds.Count:" + receivedMessageIds.Count);

			// send fastack for all message which are completely received, to disque node.
			
			var resultLen = ackStream.SendStringCommand(DISQUE_COMMAND_FASTACK, string.Empty, string.Empty, receivedMessageIds.ToArray());
			if (0 < resultLen) {
				var firstByte = ackStream.ReadFirstByte();
				switch (firstByte) {
					case 45: {// '-' error
						var errorStr = Encoding.UTF8.GetString(ackStream.ReadLineBytes());
						// XrossPeer.Log("fastack error:" + errorStr);
						break;
					}
					case 58: {// ':' integer
						var resultStr = Encoding.UTF8.GetString(ackStream.ReadLineBytes());
						// XrossPeer.Log("fastack int result:" + resultStr);
						break;
					}
					default: {
						// XrossPeer.Log("fastack unkwnown result:" + firstByte + " char:" + Convert.ToChar(firstByte));
						break;
					}
				}
			}
			
			receivedMessageIds.Clear();

			// input datas to act.
			InputDatasToContext(receivedDatas);
			receivedDatas.Clear();
		}
		return true;
	}

	
	public void Dispose () {
		addStream.Close();
		addStream = null;

		getStream.Close();
		getStream = null;

		ackStream.Close();
		ackStream = null;

		// XrossPeer.Log("disque connections disposed");
	}

	public class ResponseException : Exception {
		public ResponseException (string code) : base ("Response error") {
			Code = code;
		}

		public string Code { get; private set; }
	}

}

/**
	disque接続用のnetwork stream
*/
public class DisqueConnectionSocket {
	private readonly string info;

	const int bufferSize = 1024;// bytes, 1K
	private byte[] buf = new byte[bufferSize];

	private Socket sock;

	private readonly byte[] bytes_aster;
	private readonly byte[] bytes_r_n;
	private readonly byte[] bytes_dollar;

	/**
		コンストラクタ
	*/
	public DisqueConnectionSocket (string info, string host, int port, int timeout) {
		this.info = info;

		this.sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		this.sock.NoDelay = true;
		this.sock.SendTimeout = timeout;

		this.bytes_aster = Encoding.UTF8.GetBytes("*");
		this.bytes_r_n = Encoding.UTF8.GetBytes("\r\n");
		this.bytes_dollar = Encoding.UTF8.GetBytes("$");

		try {
			this.sock.Connect(host, port);
		} catch (Exception e) {
			// XrossPeer.Log("ERROR: DisqueConnectionController: failed to connect to disque @:" + host + ":" + port);
			// XrossPeer.Log("ERROR: reason:" + e);
		}
		
		if (!this.sock.Connected) {
			// failed to create connection. 
			this.sock.Close();
			this.sock = null;
			throw new Exception("failed to connect to disque @:" + host + ":" + port);
		}
	}

	public void Close () {
		this.sock.Close();
		this.sock = null;
	}

	/**
		send byte data to server with disque format.
	*/
	public int SendByteCommand (string cmd, string queueId, byte[] data, params string[] options) {
		if (sock == null) return 0;

		var byteBuffer = new MemoryStream();

		var contentCount = 1;// count of command.
		
		if (!string.IsNullOrEmpty(queueId)) {
			contentCount++;
		}

		if (0 < data.Length) {
			contentCount++;
		}

		if (0 < options.Length) {
			contentCount = contentCount + options.Length;
		}

		// "*" + contentCount.ToString() + "\r\n"
		{
			var contentCountBytes = Encoding.UTF8.GetBytes(contentCount.ToString());
			
			byteBuffer.Write(bytes_aster, 0, bytes_aster.Length);
			byteBuffer.Write(contentCountBytes, 0, contentCountBytes.Length);
			byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
		}

		// "$" + cmd.Length + "\r\n" + cmd + "\r\n"
		{
			var commandBytes = Encoding.UTF8.GetBytes(cmd);
			var commandCountBytes = Encoding.UTF8.GetBytes(cmd.Length.ToString());
		
			byteBuffer.Write(bytes_dollar, 0, bytes_dollar.Length);
			byteBuffer.Write(commandCountBytes, 0, commandCountBytes.Length);
			byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
			byteBuffer.Write(commandBytes, 0, commandBytes.Length);
			byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
		}

		// "$" + queueId.Length + "\r\n" + queueId + "\r\n"
		if (!string.IsNullOrEmpty(queueId)) {
			var queueIdBytes = Encoding.UTF8.GetBytes(queueId);
			var queueIdCountBytes = Encoding.UTF8.GetBytes(queueId.Length.ToString());
			
			byteBuffer.Write(bytes_dollar, 0, bytes_dollar.Length);
			byteBuffer.Write(queueIdCountBytes, 0, queueIdCountBytes.Length);
			byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
			byteBuffer.Write(queueIdBytes, 0, queueIdBytes.Length);
			byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
		}

		// "$" + data.Length + "\r\n" + data + "\r\n"
		if (0 < data.Length) {
			var dataCountBytes = Encoding.UTF8.GetBytes(data.Length.ToString());
			
			byteBuffer.Write(bytes_dollar, 0, bytes_dollar.Length);
			byteBuffer.Write(dataCountBytes, 0, dataCountBytes.Length);
			byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
			byteBuffer.Write(data, 0, data.Length);
			byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
		}

		// "$" + option.Length + "\r\n" + option + "\r\n"
		if (0 < options.Length) {
			foreach (var option in options) {
				var optionBytes = Encoding.UTF8.GetBytes(option.ToString());
				var optionCountBytes = Encoding.UTF8.GetBytes(option.Length.ToString());
			
				byteBuffer.Write(bytes_dollar, 0, bytes_dollar.Length);
				byteBuffer.Write(optionCountBytes, 0, optionCountBytes.Length);
				byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
				byteBuffer.Write(optionBytes, 0, optionBytes.Length);
				byteBuffer.Write(bytes_r_n, 0, bytes_r_n.Length);
			}	
		}

		try {
			sock.Send(byteBuffer.ToArray());

			return sock.Available;
		} catch (Exception e) {
			// XrossPeer.LogError("socket:" + info + " error:" + e);
			
			sock.Close();
			sock = null;
		}

		return 0;
	}

	/**
		send string data to server with disque format.
	*/
	public int SendStringCommand (string cmd, string queueId, string data, params string[] options) {
		return SendByteCommand(cmd, queueId, Encoding.UTF8.GetBytes(data), options);
	}
	
	public byte ReadFirstByte () {
		byte[] b = new byte[1];
		sock.Receive(b);
		return b[0];
	}

	public byte[] ReadLineBytes () {
		byte[] b = new byte[1];
		
		int limit = sock.Available;

		int i = 0;
		while (true) {
			sock.Receive(b);
			if (b[0] == '\r') continue;
			if (b[0] == '\n') break;

			buf[i] = b[0];
			
			if (i == limit) {
				// XrossPeer.Log("limit by Available.");
				break;
			}

			if (i == buf.Length) {
				// XrossPeer.Log("too large line.");
				break;
			}

			i++;
		}
		var retByte = new byte[i];
		Array.Copy(buf, 0, retByte, 0, i);

		return retByte;
	}

	public byte[] ReadBytes (int length) {
		byte[] retByte = new byte[length];
		
		int limit = sock.Available;
		if (limit < length + 2) {
			throw new Exception("failed to receive request length. too long for receive.");
		}

		sock.Receive(retByte);
		limit = sock.Available;

		byte[] b = new byte[1];
		sock.Receive(b);
		sock.Receive(b);

		return retByte;
	}
}