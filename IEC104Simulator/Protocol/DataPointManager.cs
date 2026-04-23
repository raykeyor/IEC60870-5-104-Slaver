using System.Collections.Concurrent;

namespace IEC104Simulator.Protocol;

public class DataPointManager
{
    private readonly ConcurrentDictionary<int, DataPoint> _points = new();

    public DataPoint? Get(int ioa) => _points.TryGetValue(ioa, out var dp) ? dp : null;

    public DataPoint Set(DataPoint dp)
    {
        _points[dp.Ioa] = dp;
        return dp;
    }

    public bool Remove(int ioa) => _points.TryRemove(ioa, out _);

    public IReadOnlyList<DataPoint> GetAll() => _points.Values.OrderBy(d => d.Ioa).ToList();

    public IReadOnlyList<DataPoint> GetByTypeRange(byte minType, byte maxType) =>
        _points.Values.Where(d => d.TypeId >= minType && d.TypeId <= maxType)
                      .OrderBy(d => d.Ioa).ToList();

    public IReadOnlyList<DataPoint> GetMonitoringPoints() =>
        _points.Values.Where(d => !d.IsCommand).OrderBy(d => d.Ioa).ToList();

    public IReadOnlyList<DataPoint> GetCommandPoints() =>
        _points.Values.Where(d => d.IsCommand).OrderBy(d => d.Ioa).ToList();

    public void InitDefaults()
    {
        // Single-point (M_SP_NA_1) — GroupId=1 (第1组: 开关量遥信)
        Set(new DataPoint { Ioa = 1, TypeId = 1, Name = "断路器状态1", Value = 0, GroupId = 1 });
        Set(new DataPoint { Ioa = 2, TypeId = 1, Name = "断路器状态戀1", Value = 1, GroupId = 1 });
        Set(new DataPoint { Ioa = 3, TypeId = 1, Name = "隔离开关状态", Value = 0, GroupId = 1 });
        // Single-point with time (M_SP_TB_1 = 30)
        Set(new DataPoint { Ioa = 10, TypeId = 30, Name = "带时标单点1", Value = 0, GroupId = 1 });
        Set(new DataPoint { Ioa = 11, TypeId = 30, Name = "带时标单点2", Value = 1, GroupId = 1 });
        // Double-point (M_DP_NA_1 = 3)
        Set(new DataPoint { Ioa = 20, TypeId = 3, Name = "双点遥信1", Value = 1, GroupId = 1 });
        Set(new DataPoint { Ioa = 21, TypeId = 3, Name = "双点遥信2", Value = 2, GroupId = 1 });
        // Double-point with time (M_DP_TB_1 = 31)
        Set(new DataPoint { Ioa = 30, TypeId = 31, Name = "带时标双点1", Value = 1, GroupId = 1 });
        Set(new DataPoint { Ioa = 31, TypeId = 31, Name = "带时标双点2", Value = 2, GroupId = 1 });
        // Step position (M_ST_NA_1 = 5)
        Set(new DataPoint { Ioa = 40, TypeId = 5, Name = "档位信息1", Value = 3 });
        Set(new DataPoint { Ioa = 41, TypeId = 5, Name = "档位信息2", Value = -5 });
        // Step position with time (M_ST_TB_1 = 32)
        Set(new DataPoint { Ioa = 50, TypeId = 32, Name = "带时标档位1", Value = 2 });
        // Bitstring (M_BO_NA_1 = 7)
        Set(new DataPoint { Ioa = 60, TypeId = 7, Name = "32位串1", Value = 0x0000FFFF });
        Set(new DataPoint { Ioa = 61, TypeId = 7, Name = "32位串2", Value = 0xA5A5A5A5 });
        // Bitstring with time (M_BO_TB_1 = 33)
        Set(new DataPoint { Ioa = 70, TypeId = 33, Name = "带时标32位串", Value = 0x12345678 });
        // Normalised (M_ME_NA_1 = 9) — GroupId=2 (第2组: 模拟量遥测)
        Set(new DataPoint { Ioa = 100, TypeId = 9, Name = "归一化值1", Value = 0.5, GroupId = 2 });
        Set(new DataPoint { Ioa = 101, TypeId = 9, Name = "归一化值2", Value = -0.3, GroupId = 2 });
        // Normalised with time (M_ME_TD_1 = 34)
        Set(new DataPoint { Ioa = 110, TypeId = 34, Name = "带时标归一化值", Value = 0.8, GroupId = 2 });
        // Normalised no quality (M_ME_ND_1 = 21)
        Set(new DataPoint { Ioa = 115, TypeId = 21, Name = "无质量归一化值", Value = 0.25, GroupId = 2 });
        // Scaled (M_ME_NB_1 = 11)
        Set(new DataPoint { Ioa = 120, TypeId = 11, Name = "标度值-电流", Value = 1250, GroupId = 2 });
        Set(new DataPoint { Ioa = 121, TypeId = 11, Name = "标度值-电压", Value = -800, GroupId = 2 });
        // Scaled with time (M_ME_TE_1 = 35)
        Set(new DataPoint { Ioa = 130, TypeId = 35, Name = "带时标标度值", Value = 3200, GroupId = 2 });
        // Float (M_ME_NC_1 = 13)
        Set(new DataPoint { Ioa = 140, TypeId = 13, Name = "浮点值-功率", Value = 123.45, GroupId = 2 });
        Set(new DataPoint { Ioa = 141, TypeId = 13, Name = "浮点值-频率", Value = 49.98, GroupId = 2 });
        // Float with time (M_ME_TF_1 = 36)
        Set(new DataPoint { Ioa = 150, TypeId = 36, Name = "带时标浮点值", Value = 220.1, GroupId = 2 });
        // Integrated totals (M_IT_NA_1 = 15) — GroupId=3 (第3组: 电能量)
        Set(new DataPoint { Ioa = 160, TypeId = 15, Name = "累计量-有功电能", Value = 98765, GroupId = 3 });
        Set(new DataPoint { Ioa = 161, TypeId = 15, Name = "累计量-无功电能", Value = 12345, GroupId = 3 });
        // Integrated totals with time (M_IT_TB_1 = 37)
        Set(new DataPoint { Ioa = 170, TypeId = 37, Name = "带时标累计量", Value = 543210, GroupId = 3 });
        // Packed single-point (M_PS_NA_1 = 20)
        Set(new DataPoint { Ioa = 180, TypeId = 20, Name = "成组单点信息", Value = 0xAAAA });

        // Command points
        Set(new DataPoint { Ioa = 1001, TypeId = 45, Name = "单命令-断路器合", Value = 0, IsCommand = true });
        Set(new DataPoint { Ioa = 1002, TypeId = 45, Name = "单命令-断路器分", Value = 0, IsCommand = true });
        Set(new DataPoint { Ioa = 1010, TypeId = 58, Name = "带时标单命令", Value = 0, IsCommand = true });
        Set(new DataPoint { Ioa = 1020, TypeId = 46, Name = "双命令1", Value = 1, IsCommand = true });
        Set(new DataPoint { Ioa = 1021, TypeId = 46, Name = "双命令2", Value = 2, IsCommand = true });
        Set(new DataPoint { Ioa = 1030, TypeId = 59, Name = "带时标双命令", Value = 1, IsCommand = true });
        Set(new DataPoint { Ioa = 1040, TypeId = 47, Name = "步调节命令", Value = 1, IsCommand = true });
        Set(new DataPoint { Ioa = 1050, TypeId = 60, Name = "带时标步调节", Value = 2, IsCommand = true });
        Set(new DataPoint { Ioa = 1060, TypeId = 48, Name = "设定归一化命令", Value = 0.5, IsCommand = true });
        Set(new DataPoint { Ioa = 1070, TypeId = 49, Name = "设定标度值命令", Value = 1000, IsCommand = true });
        Set(new DataPoint { Ioa = 1080, TypeId = 50, Name = "设定浮点命令", Value = 100.0, IsCommand = true });
        Set(new DataPoint { Ioa = 1090, TypeId = 51, Name = "32位串命令", Value = 0xFFFF0000, IsCommand = true });
        Set(new DataPoint { Ioa = 1061, TypeId = 61, Name = "带时标设定归一化", Value = 0.3, IsCommand = true });
        Set(new DataPoint { Ioa = 1071, TypeId = 62, Name = "带时标设定标度值", Value = 500, IsCommand = true });
        Set(new DataPoint { Ioa = 1081, TypeId = 63, Name = "带时标设定浮点", Value = 50.0, IsCommand = true });
        Set(new DataPoint { Ioa = 1091, TypeId = 64, Name = "带时标32位串命令", Value = 0x00FF00FF, IsCommand = true });
    }
}
