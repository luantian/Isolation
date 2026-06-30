#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成测试数据包脚本
创建符合软件导入格式的测试数据文件
"""

import os
import random
from datetime import datetime, timedelta

# 测试数据配置
test_projects = [
    {
        "name": "TP田湾核电",
        "code": "TP",
        "units": [
            {
                "name": "TP-3田湾3号机组",
                "code": "TP-3",
                "systems": [
                    {
                        "name": "SGT蒸汽发生器给水系统",
                        "code": "SGT",
                        "penetrations": [
                            {
                                "name": "PEN600贯穿件",
                                "code": "PEN600",
                                "valves": [
                                    {"code": "3SGT100VP", "name": "电动隔离阀", "device": "DEV-010"},
                                    {"code": "3SGT101VP", "name": "止回阀", "device": "DEV-011"},
                                    {"code": "3SGT102VP", "name": "闸阀", "device": "DEV-012"},
                                    {"code": "3SGT103VP", "name": "气动阀", "device": "DEV-013"},
                                ]
                            },
                            {
                                "name": "PEN601贯穿件",
                                "code": "PEN601",
                                "valves": [
                                    {"code": "3SGT110VP", "name": "电动隔离阀", "device": "DEV-014"},
                                    {"code": "3SGT111VP", "name": "止回阀", "device": "DEV-015"},
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "TP-4田湾4号机组",
                "code": "TP-4",
                "systems": [
                    {
                        "name": "CVS化学和容积控制系统",
                        "code": "CVS",
                        "penetrations": [
                            {
                                "name": "PEN610贯穿件",
                                "code": "PEN610",
                                "valves": [
                                    {"code": "4CVS200VP", "name": "电动隔离阀", "device": "DEV-020"},
                                    {"code": "4CVS201VP", "name": "止回阀", "device": "DEV-021"},
                                    {"code": "4CVS202VP", "name": "闸阀", "device": "DEV-022"},
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "ZP漳州核电",
        "code": "ZP",
        "units": [
            {
                "name": "ZP-1漳州1号机组",
                "code": "ZP-1",
                "systems": [
                    {
                        "name": "APA主给水系统",
                        "code": "APA",
                        "penetrations": [
                            {
                                "name": "PEN500贯穿件",
                                "code": "PEN500",
                                "valves": [
                                    {"code": "1APA300VP", "name": "电动隔离阀", "device": "DEV-030"},
                                    {"code": "1APA301VP", "name": "闸阀", "device": "DEV-031"},
                                    {"code": "1APA302VP", "name": "止回阀", "device": "DEV-032"},
                                    {"code": "1APA303VP", "name": "气动阀", "device": "DEV-033"},
                                    {"code": "1APA304VP", "name": "调节阀", "device": "DEV-034"},
                                ]
                            },
                            {
                                "name": "PEN501贯穿件",
                                "code": "PEN501",
                                "valves": [
                                    {"code": "1APA310VP", "name": "电动隔离阀", "device": "DEV-035"},
                                    {"code": "1APA311VP", "name": "止回阀", "device": "DEV-036"},
                                ]
                            }
                        ]
                    },
                    {
                        "name": "GRE汽轮机调节系统",
                        "code": "GRE",
                        "penetrations": [
                            {
                                "name": "PEN510贯穿件",
                                "code": "PEN510",
                                "valves": [
                                    {"code": "1GRE400VP", "name": "电动隔离阀", "device": "DEV-040"},
                                    {"code": "1GRE401VP", "name": "闸阀", "device": "DEV-041"},
                                ]
                            }
                        ]
                    }
                ]
            },
            {
                "name": "ZP-2漳州2号机组",
                "code": "ZP-2",
                "systems": [
                    {
                        "name": "APA主给水系统",
                        "code": "APA",
                        "penetrations": [
                            {
                                "name": "PEN520贯穿件",
                                "code": "PEN520",
                                "valves": [
                                    {"code": "2APA500VP", "name": "电动隔离阀", "device": "DEV-050"},
                                    {"code": "2APA501VP", "name": "止回阀", "device": "DEV-051"},
                                    {"code": "2APA502VP", "name": "闸阀", "device": "DEV-052"},
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    },
    {
        "name": "FN防城港核电",
        "code": "FN",
        "units": [
            {
                "name": "FN-3防城港3号机组",
                "code": "FN-3",
                "systems": [
                    {
                        "name": "RCP反应堆冷却剂系统",
                        "code": "RCP",
                        "penetrations": [
                            {
                                "name": "PEN700贯穿件",
                                "code": "PEN700",
                                "valves": [
                                    {"code": "3RCP600VP", "name": "电动隔离阀", "device": "DEV-060"},
                                    {"code": "3RCP601VP", "name": "止回阀", "device": "DEV-061"},
                                    {"code": "3RCP602VP", "name": "闸阀", "device": "DEV-062"},
                                ]
                            },
                            {
                                "name": "PEN701贯穿件",
                                "code": "PEN701",
                                "valves": [
                                    {"code": "3RCP610VP", "name": "电动隔离阀", "device": "DEV-063"},
                                    {"code": "3RCP611VP", "name": "气动阀", "device": "DEV-064"},
                                ]
                            }
                        ]
                    }
                ]
            }
        ]
    }
]

# 生成测试日期（最近几个月）
def generate_test_dates():
    """生成多个测试日期"""
    dates = []
    base_date = datetime(2026, 7, 1, 9, 0, 0)

    # 为每个阀门生成2-5个不同日期的测试数据
    for i in range(60):
        test_date = base_date + timedelta(days=random.randint(0, 90))
        test_date = test_date.replace(hour=random.randint(8, 17), minute=0, second=0)
        dates.append(test_date)

    return dates

def generate_process_data(test_time, base_pressure, base_flow):
    """生成过程数据（60个数据点）"""
    lines = ['"导出时间","实时压力P1","瞬时流量M1","瞬时流量M2","温度T_R","压力P2_R"']

    for i in range(60):
        current_time = test_time + timedelta(seconds=i)

        # 模拟压力上升过程
        progress = i / 60.0
        p1 = base_pressure * progress + random.uniform(-0.05, 0.05)

        # 模拟流量下降过程
        m1 = base_flow * (1 - progress / 2) + random.uniform(-0.3, 0.3)
        m2 = 2.5 + random.uniform(-0.1, 0.1)

        # 温度轻微波动
        tr = 24.0 + random.uniform(-0.5, 0.5)

        # P2压力约为P1的20%
        p2r = p1 * 0.2 + random.uniform(-0.03, 0.03)

        line = f'"{current_time.strftime("%Y-%m-%d %H:%M:%S")}",{p1:.6f},{m1:.6f},{m2:.6f},{tr:.6f},{p2r:.6f}'
        lines.append(line)

    return '\n'.join(lines)

def generate_summary_data(valve_code, device_code, test_time, pressure, leakage, result):
    """生成结果汇总数据"""
    return f"""试验对象编码,{valve_code}
测量装置编号,{device_code}
试验时间,{test_time.strftime("%Y-%m-%d %H:%M:%S")}
试验压力,{pressure:.1f}
最终泄漏率,{leakage:.4f}
判定结果,{result}"""

def create_test_data(base_dir):
    """创建测试数据文件"""
    test_dates = generate_test_dates()
    date_index = 0

    for project in test_projects:
        project_dir = os.path.join(base_dir, project["name"])

        for unit in project["units"]:
            unit_dir = os.path.join(project_dir, unit["name"])

            for system in unit["systems"]:
                system_dir = os.path.join(unit_dir, system["name"])

                for penetration in system["penetrations"]:
                    pen_dir = os.path.join(system_dir, penetration["name"])

                    for valve in penetration["valves"]:
                        valve_dir = os.path.join(pen_dir, f"{valve['code']}{valve['name']}")
                        os.makedirs(valve_dir, exist_ok=True)

                        # 为每个阀门生成2-4个测试数据
                        num_tests = random.randint(2, 4)

                        for _ in range(num_tests):
                            test_time = test_dates[date_index % len(test_dates)]
                            date_index += 1

                            # 生成测试参数
                            pressure = round(random.uniform(1.5, 2.0), 1)
                            leakage = round(random.uniform(0.5, 3.5), 4)
                            result = "合格" if leakage < 3.0 else "不合格"

                            # 生成文件名
                            date_code = test_time.strftime("%m%d")
                            summary_file = os.path.join(valve_dir, f"{valve['code']}_{date_code}_结果汇总.csv")
                            process_file = os.path.join(valve_dir, f"{valve['code']}_{date_code}_过程数据.csv")

                            # 写入结果汇总文件
                            summary_data = generate_summary_data(
                                valve['code'], valve['device'], test_time,
                                pressure, leakage, result
                            )
                            with open(summary_file, 'w', encoding='gbk') as f:
                                f.write(summary_data)

                            # 写入过程数据文件
                            process_data = generate_process_data(test_time, pressure, 24.0)
                            with open(process_file, 'w', encoding='utf-8') as f:
                                f.write(process_data)

                            print(f"[OK] 生成: {valve['code']}_{date_code} (泄漏率: {leakage:.4f}, 结果: {result})")

if __name__ == "__main__":
    base_dir = os.path.dirname(os.path.abspath(__file__))
    print(f"开始生成测试数据到: {base_dir}")
    print(f"将生成 {sum(len(u['systems']) for p in test_projects for u in p['units'])} 个系统")
    print()

    create_test_data(base_dir)

    print(f"[OK] 测试数据生成完成！")
