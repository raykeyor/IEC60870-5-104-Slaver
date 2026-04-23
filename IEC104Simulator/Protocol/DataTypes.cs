namespace IEC104Simulator.Protocol;

public class DataPoint
{
    public int Ioa { get; set; }
    public byte TypeId { get; set; }
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public byte Quality { get; set; }
    public bool IsCommand { get; set; }
    public int GroupId { get; set; } = 0;  // 0=无分组(仅响应全站总召); 1-16=所属分组(响应QOI=21-36分组召唤)
    public DateTime LastUpdate { get; set; } = DateTime.Now;
    public string TypeName => TypeIdHelper.GetName(TypeId);
}

public class CommandRecord
{
    public int Id { get; set; }
    public int Ioa { get; set; }
    public byte TypeId { get; set; }
    public string TypeName { get; set; } = "";
    public double Value { get; set; }
    public int Qu { get; set; }
    public bool IsSelect { get; set; }
    public string CotName { get; set; } = "";
    public string Status { get; set; } = "";
    public string ConnInfo { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public string? PendingId { get; set; }  // set while waiting for user decision
}

public class LogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string ConnInfo { get; set; } = "";  // 来源主站地址，空=系统级事件
}

public class CommRecord
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Direction { get; set; } = "rx";   // "rx" = from master, "tx" = to master
    public string TypeName { get; set; } = "";
    public int Ioa { get; set; }
    public string Value { get; set; } = "";
    public string Cot { get; set; } = "";
    public string Detail { get; set; } = "";
    public string ConnInfo { get; set; } = "";
    // Decoded APDU fields
    public int TypeId { get; set; }       // TI type identification number
    public int CotCode { get; set; }      // COT as integer
    public int Ca { get; set; }           // Common Address
    public bool IsNeg { get; set; }       // PN negative flag
    public bool IsTest { get; set; }      // T test flag
    public int Oa { get; set; }           // Originator Address
    public int NumberOfElem { get; set; } // Number of elements (VSQ INFONUM)
    public bool IsSeq { get; set; }       // VSQ SQ flag
    public string FullDecode { get; set; } = ""; // Complete human-readable ASDU decode
}

public class ConnectionInfo
{
    public string RemoteAddress { get; set; } = "";
    public bool Active { get; set; }
}

public class TlsConfig
{
    public bool Enabled { get; set; }
    /// <summary>服务端证书 + 私钥 (PKCS12 / .pfx)</summary>
    public string PfxPath { get; set; } = "";
    public string PfxPassword { get; set; } = "";
    /// <summary>验证主站客户端证书所用的 CA 证书 (PEM/DER)，空则不验证</summary>
    public string CaCertPath { get; set; } = "";
    /// <summary>是否要求主站提供并验证客户端证书</summary>
    public bool RequireClientCert { get; set; }
}

public class ApciConfig
{
    public int K  { get; set; } = 12;  // 发送窗口: 最大未确认I帧数 (1-32767, 标准默认12)
    public int W  { get; set; } = 8;   // 接收窗口: 最多W帧后回确认 (1-32767, 标准默认8)
    public int T0 { get; set; } = 30;  // 连接建立超时 (s, 标准默认30)
    public int T1 { get; set; } = 15;  // I/U帧发送未确认超时 (s, 超时则断开, 标准默认15)
    public int T2 { get; set; } = 10;  // 收到数据后回确认超时 (s, 必须 < T1, 标准默认10)
    public int T3 { get; set; } = 20;  // 空闲连接测试帧间隔 (s, 超时发TESTFR, 标准默认20)
}

public static class TypeIdHelper
{
    private static readonly Dictionary<byte, string> Names = new()
    {
        [1] = "M_SP_NA_1", [3] = "M_DP_NA_1", [5] = "M_ST_NA_1",
        [7] = "M_BO_NA_1", [9] = "M_ME_NA_1", [11] = "M_ME_NB_1",
        [13] = "M_ME_NC_1", [15] = "M_IT_NA_1", [20] = "M_PS_NA_1",
        [21] = "M_ME_ND_1",
        [30] = "M_SP_TB_1", [31] = "M_DP_TB_1", [32] = "M_ST_TB_1",
        [33] = "M_BO_TB_1", [34] = "M_ME_TD_1", [35] = "M_ME_TE_1",
        [36] = "M_ME_TF_1", [37] = "M_IT_TB_1",
        [45] = "C_SC_NA_1", [46] = "C_DC_NA_1", [47] = "C_RC_NA_1",
        [48] = "C_SE_NA_1", [49] = "C_SE_NB_1", [50] = "C_SE_NC_1",
        [51] = "C_BO_NA_1",
        [58] = "C_SC_TA_1", [59] = "C_DC_TA_1", [60] = "C_RC_TA_1",
        [61] = "C_SE_TA_1", [62] = "C_SE_TB_1", [63] = "C_SE_TC_1",
        [64] = "C_BO_TA_1",
        [100] = "C_IC_NA_1", [101] = "C_CI_NA_1", [102] = "C_RD_NA_1",
        [103] = "C_CS_NA_1", [104] = "C_TS_NA_1", [105] = "C_RP_NA_1",
    };

    public static string GetName(byte typeId) =>
        Names.TryGetValue(typeId, out var n) ? n : $"TYPE_{typeId}";

    public static bool IsMonitoringType(byte typeId) =>
        (typeId >= 1 && typeId <= 21) || (typeId >= 30 && typeId <= 40);

    public static bool IsCommandType(byte typeId) =>
        (typeId >= 45 && typeId <= 51) || (typeId >= 58 && typeId <= 64);
}
