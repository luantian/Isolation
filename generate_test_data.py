"""
生成海南3机组测试数据 - 用于验证批量导入功能
参考图片：安全壳隔离阀密封性试验记录

文件夹结构：根文件夹\项目\机组\系统\贯穿件\阀门\数据包文件
"""
import os
import json
from datetime import datetime

# 输出目录
OUTPUT_DIR = os.path.join(os.path.dirname(__file__), "海南3机组测试数据")

# 测试数据（参考用户提供的图片）
# (对象编码, 对象名称, 贯穿件, 贯穿件名称, 试验压力, 泄漏率, 结果, 试验时间)
TEST_DATA = [
    # PN217下的阀门
    ("3CAM003VA", "隔离阀", "PN217", "贯穿件PN217", 0.423, 0.035, "合格", "2025-07-09 08:30:00"),
    ("3CAM005VA", "隔离阀", "PN217", "贯穿件PN217", 0.425, 0.032, "合格", "2025-07-09 09:15:00"),
    # PN218下的阀门
    ("3CAM004VA", "隔离阀", "PN218", "贯穿件PN218", 0.430, 0.028, "合格", "2025-07-09 10:00:00"),
    ("3CAM006VA", "隔离阀", "PN218", "贯穿件PN218", 0.427, 0.041, "合格", "2025-07-09 10:45:00"),
    # PN219下的阀门
    ("3CAM007VA", "隔离阀", "PN219", "贯穿件PN219", 0.421, 0.038, "合格", "2025-07-10 08:30:00"),
    ("3CAM009VA", "隔离阀", "PN219", "贯穿件PN219", 0.425, 0.025, "合格", "2025-07-09 14:00:00"),
    # PN220下的阀门
    ("3CAM008VA", "隔离阀", "PN220", "贯穿件PN220", 0.421, 0.044, "合格", "2025-07-10 09:00:00"),
    ("3CAM010VA", "隔离阀", "PN220", "贯穿件PN220", 0.427, 0.029, "合格", "2025-07-09 15:30:00"),
    # PN236下的阀门
    ("3CAM073VA", "隔离阀", "PN236", "贯穿件PN236", 0.431, 0.012, "合格", "2025-07-11 08:30:00"),
    # PN313A下的阀门
    ("3CAM059VA", "隔离阀", "PN313A", "贯穿件PN313A", 0.430, 0.008, "合格", "2025-07-07 08:30:00"),
    ("3CAM042VA", "隔离阀", "PN313A", "贯穿件PN313A", 0.430, 0.015, "合格", "2025-07-07 09:15:00"),
    ("3CAM043VA", "隔离阀", "PN313A", "贯穿件PN313A", 0.430, 0.018, "合格", "2025-07-07 10:00:00"),
    # PN313B下的阀门
    ("3CAM060VA", "隔离阀", "PN313B", "贯穿件PN313B", 0.429, 0.011, "合格", "2025-07-07 10:45:00"),
    ("3CAM044VA", "隔离阀", "PN313B", "贯穿件PN313B", 0.429, 0.022, "合格", "2025-07-07 11:30:00"),
    ("3CAM045VA", "隔离阀", "PN313B", "贯穿件PN313B", 0.429, 0.019, "合格", "2025-07-07 14:00:00"),
]


def create_json_package(obj_code, device_code, test_time, test_pressure, leakage_rate, result):
    """创建 JSON 格式的数据包"""
    # 生成200个采样点的曲线数据
    pressure_data = [test_pressure * (0.95 + 0.1 * (i/200)) for i in range(200)]
    flow_data = [leakage_rate * (0.9 + 0.2 * (i/200)) for i in range(200)]
    temp_data = [24.5 + 0.1 * (i/200) for i in range(200)]

    package = {
        "ObjectCode": obj_code,
        "DeviceCode": device_code,
        "TestTime": test_time.strftime("%Y-%m-%d %H:%M:%S"),
        "TestPressure": test_pressure,
        "LeakageRate": leakage_rate,
        "Result": "Pass" if result == "合格" else "Fail",
        "PressureCurve": {
            "Unit": "MPa",
            "Data": pressure_data
        },
        "FlowCurve": {
            "Unit": "L/min",
            "Data": flow_data
        },
        "TempCurve": {
            "Unit": "°C",
            "Data": temp_data
        }
    }
    return json.dumps(package, ensure_ascii=False, indent=2)


def create_csv_curve(obj_code, device_code, test_time, test_pressure, leakage_rate):
    """创建 CSV 曲线文件"""
    lines = ["时间 (s),P1 (MPa),M1 (L/min),T (°C)"]
    for i in range(200):
        t = i * 0.5
        p = test_pressure * (0.95 + 0.1 * (i/200))
        f = leakage_rate * (0.9 + 0.2 * (i/200))
        temp = 24.5 + 0.1 * (i/200)
        lines.append(f"{t:.1f},{p:.6f},{f:.6f},{temp:.2f}")
    return "\n".join(lines)


def create_summary_csv(obj_code, device_code, test_time, test_pressure, leakage_rate, result):
    """创建结果汇总 CSV（与现有格式一致）"""
    result_text = "合格" if result == "合格" else "不合格"
    # 使用与现有脚本相同的格式
    content = f"判定结果，{result_text}\n最终泄漏率，{leakage_rate:.4f}\n试验对象编码，{obj_code}\n测量装置编号，{device_code}\n试验时间，{test_time.strftime('%Y-%m-%d %H:%M:%S')}\n试验压力，{test_pressure}"
    content = content.replace('，', ',')
    return content


def main():
    # 清理旧目录
    if os.path.exists(OUTPUT_DIR):
        import shutil
        shutil.rmtree(OUTPUT_DIR)

    device_code = "DEV-001"  # 使用数据库中已有的设备

    for obj_code, obj_name, penetration, pen_name, pressure, rate, result, test_time_str in TEST_DATA:
        test_time = datetime.strptime(test_time_str, "%Y-%m-%d %H:%M:%S")
        date_suffix = test_time.strftime("%m%d")

        # 创建文件夹结构：项目\机组\系统\贯穿件\阀门
        folder_path = os.path.join(
            OUTPUT_DIR,
            "HN_海南核电",
            "HN-3_海南3号机组",
            "CAM_安全壳系统",
            f"{penetration}_{pen_name}",
            f"{obj_code}_{obj_name}"
        )
        os.makedirs(folder_path, exist_ok=True)

        # 创建 JSON 数据包
        json_content = create_json_package(obj_code, device_code, test_time, pressure, rate, result)
        json_file = os.path.join(folder_path, f"{obj_code}_{date_suffix}_数据包.json")
        with open(json_file, "w", encoding="utf-8") as f:
            f.write(json_content)

        # 创建 CSV 曲线文件
        csv_content = create_csv_curve(obj_code, device_code, test_time, pressure, rate)
        csv_file = os.path.join(folder_path, f"{obj_code}_{date_suffix}_过程数据.csv")
        with open(csv_file, "w", encoding="gbk") as f:
            f.write(csv_content)

        # 创建结果汇总 CSV
        summary_content = create_summary_csv(obj_code, device_code, test_time, pressure, rate, result)
        summary_file = os.path.join(folder_path, f"{obj_code}_{date_suffix}_结果汇总.csv")
        with open(summary_file, "w", encoding="gbk") as f:
            f.write(summary_content)

        print(f"  已创建：{obj_code} ({test_time.strftime('%Y-%m-%d')})")

    print(f"\n 测试数据生成完成！")
    print(f" 输出目录：{OUTPUT_DIR}")
    print(f" 共 {len(TEST_DATA)} 条试验记录")
    print(f"\n使用说明:")
    print(f"1. 打开基础台账 -> 项目机组管理")
    print(f"2. 点击「批量导入」按钮")
    print(f"3. 选择生成的文件夹")
    print(f"4. 系统会自动解析并导入数据")
    print(f"\n注意事项:")
    print(f"- 确保数据库中有设备编码：{device_code}")
    print(f"- 导入时会自动创建项目、机组、路径节点")
    print(f"- 所有记录都是合格状态")


if __name__ == "__main__":
    main()
