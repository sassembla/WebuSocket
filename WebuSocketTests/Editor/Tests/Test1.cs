using System;
using System.Collections.Generic;
using UnityEngine;
using WebuSocketCore;

/*
	tests for 1 frame per sec.
*/

public class TestFrame {
	public const int throttleFrame = 1;
}

public class Test_1_0_OrderAndDataShouldMatchWithThrottle : ITestCase {
	public OptionalSettings OnOptionalSettings () {
        return new OptionalSettings(TestFrame.throttleFrame);
    }
	
    public void OnConnect(WebuSocket webuSocket) {
        webuSocket.Send(new byte[]{1,2,3});
    }
	
    public void OnReceived(WebuSocket webuSocket, Queue<byte[]> datas) {
        if (datas.Count == 1) {
			var data = datas.Dequeue();
			if (data[0] == 1 && data[1] == 2 && data[2] == 3) {
				webuSocket.Disconnect();
			} else {
				Debug.LogError("not match.");
			}
		}
    }
}

public class Test_1_1_SizeMatching_126WithThrottle : ITestCase {
	public OptionalSettings OnOptionalSettings () {
        return new OptionalSettings(TestFrame.throttleFrame);
    }
		
    public void OnConnect(WebuSocket webuSocket) {
		var data126 = new byte[126];
		for (var i = 0; i < data126.Length; i++) data126[i] = 1;
		webuSocket.Send(data126);
    }

    public void OnReceived(WebuSocket webuSocket, Queue<byte[]> datas) {
        if (datas.Count == 1) {
			var data = datas.Dequeue();
			if (data.Length != 126) Debug.LogError("not match.");
			webuSocket.Disconnect();
			return;
		}
		Debug.LogError("not match 2.");
    }
}

public class Test_1_2_SizeMatching_127WithThrottle : ITestCase {
	public OptionalSettings OnOptionalSettings () {
        return new OptionalSettings(TestFrame.throttleFrame);
    }
	
    public void OnConnect(WebuSocket webuSocket) {
		var data = new byte[127];
		for (var i = 0; i < data.Length; i++) data[i] = 1;
		webuSocket.Send(data);
    }

    public void OnReceived(WebuSocket webuSocket, Queue<byte[]> datas) {
        if (datas.Count == 1) {
			var data = datas.Dequeue();
			if (data.Length != 127) Debug.LogError("not match.");
			webuSocket.Disconnect();
			return;
		}
		Debug.LogError("not match 2.");
    }
}


public class Test_1_3_SizeMatching_65534WithThrottle : ITestCase {
	public OptionalSettings OnOptionalSettings () {
        return new OptionalSettings(TestFrame.throttleFrame);
    }
	
    public void OnConnect(WebuSocket webuSocket) {
		var data = new byte[65534];
		for (var i = 0; i < data.Length; i++) data[i] = 1;
		webuSocket.Send(data);
    }

    public void OnReceived(WebuSocket webuSocket, Queue<byte[]> datas) {
        if (datas.Count == 1) {
			var data = datas.Dequeue();
			if (data.Length != 65534) Debug.LogError("not match.");
			webuSocket.Disconnect();
			return;
		}
		Debug.LogError("not match 2.");
    }
}


public class Test_1_4_SizeMatching_65535WithThrottle : ITestCase {
	public OptionalSettings OnOptionalSettings () {
        return new OptionalSettings(TestFrame.throttleFrame);
    }
	
    public void OnConnect(WebuSocket webuSocket) {
		var data = new byte[65535];
		for (var i = 0; i < data.Length; i++) data[i] = 1;
		webuSocket.Send(data);
    }

    public void OnReceived(WebuSocket webuSocket, Queue<byte[]> datas) {
        if (datas.Count == 1) {
			var data = datas.Dequeue();
			if (data.Length != 65535) Debug.LogError("not match.");
			webuSocket.Disconnect();
			return;
		}
		Debug.LogError("not match 2.");
    }
}


public class Test_1_5_SizeMatching_14140WithThrottle : ITestCase {
	public OptionalSettings OnOptionalSettings () {
        return new OptionalSettings(TestFrame.throttleFrame);
    }
		
    public void OnConnect(WebuSocket webuSocket) {
		var data = new byte[14140];
		for (var i = 0; i < data.Length; i++) data[i] = (byte)i;
		webuSocket.Send(data);
    }

    public void OnReceived(WebuSocket webuSocket, Queue<byte[]> datas) {
        if (datas.Count == 1) {
			var data = datas.Dequeue();
			if (data.Length != 14140) Debug.LogError("not match.");
			webuSocket.Disconnect();
			return;
		}
		Debug.LogError("not match 2.");
    }
}