# 生成基础台账批量导入测试数据
# 文件夹结构: 根目录/项目/机组/系统/贯穿件/阀门/数据文件

$root = "F:\workspace\cechuang\projects\Isolation\测试数据"

# 清理旧数据
if (Test-Path $root) { Remove-Item $root -Recurse -Force }

# ============================================================
# 定义测试数据: 项目/机组/系统/贯穿件/阀门/试验数据
# ============================================================

$tests = @(
    # === HN-3: 安全壳系统CAM ===
    @{ Proj="HN海南核电"; Unit="HN-3海南3号机组"; Sys="CAM安全壳系统"; Pen="PN217贯穿件PN217"; Valve="3CAM003VA隔离阀"
       Device="DEV-001"; Time="2026-07-01 09:30:00"; Rate="6.600"; Result="合格"; Pressure="0.423" },
    @{ Proj="HN海南核电"; Unit="HN-3海南3号机组"; Sys="CAM安全壳系统"; Pen="PN217贯穿件PN217"; Valve="3CAM005VA隔离阀"
       Device="DEV-002"; Time="2026-07-01 10:15:00"; Rate="6.194"; Result="合格"; Pressure="0.425" },
    @{ Proj="HN海南核电"; Unit="HN-3海南3号机组"; Sys="CAM安全壳系统"; Pen="PN218贯穿件PN218"; Valve="3CAM004VA隔离阀"
       Device="DEV-001"; Time="2026-07-02 08:45:00"; Rate="5.997"; Result="合格"; Pressure="0.430" },
    @{ Proj="HN海南核电"; Unit="HN-3海南3号机组"; Sys="CAM安全壳系统"; Pen="PN236贯穿件PN236"; Valve="3CAM073VA隔离阀"
       Device="DEV-003"; Time="2026-07-03 14:00:00"; Rate="0.230"; Result="合格"; Pressure="0.431" },

    # === HN-4: 化学和容积控制系统CVS ===
    @{ Proj="HN海南核电"; Unit="HN-4海南4号机组"; Sys="CVS化学和容积控制系统"; Pen="CVS-PN101贯穿件CVS-PN101"; Valve="4CVS001VA隔离阀"
       Device="DEV-001"; Time="2026-07-05 09:00:00"; Rate="3.210"; Result="合格"; Pressure="0.415" },
    @{ Proj="HN海南核电"; Unit="HN-4海南4号机组"; Sys="CVS化学和容积控制系统"; Pen="CVS-PN102贯穿件CVS-PN102"; Valve="4CVS003VA隔离阀"
       Device="DEV-002"; Time="2026-07-06 10:30:00"; Rate="7.890"; Result="不合格"; Pressure="0.420" },

    # === ZZ-1: 余热排出系统RHR ===
    @{ Proj="ZZ漳州核电"; Unit="ZZ-1漳州1号机组"; Sys="RHR余热排出系统"; Pen="RHR-PN001贯穿件RHR-PN001"; Valve="1RHR010VP隔离阀"
       Device="DEV-002"; Time="2026-07-08 08:30:00"; Rate="4.560"; Result="合格"; Pressure="0.385" },
    @{ Proj="ZZ漳州核电"; Unit="ZZ-1漳州1号机组"; Sys="RHR余热排出系统"; Pen="RHR-PN002贯穿件RHR-PN002"; Valve="1RHR013VP隔离阀"
       Device="DEV-001"; Time="2026-07-09 11:00:00"; Rate="8.340"; Result="不合格"; Pressure="0.392" },

    # === GD-1: 凝结水系统CDS ===
    @{ Proj="GD广东核电"; Unit="GD-1广东1号机组"; Sys="CDS凝结水系统"; Pen="CDS-PN001贯穿件CDS-PN001"; Valve="1CDS001VA隔离阀"
       Device="DEV-001"; Time="2026-07-10 09:00:00"; Rate="3.450"; Result="合格"; Pressure="0.370" },
    @{ Proj="GD广东核电"; Unit="GD-1广东1号机组"; Sys="CDS凝结水系统"; Pen="CDS-PN001贯穿件CDS-PN001"; Valve="1CDS002VA隔离阀"
       Device="DEV-003"; Time="2026-07-10 14:30:00"; Rate="2.890"; Result="合格"; Pressure="0.375" },

    # === FG-1: 辅助给水系统AFW ===
    @{ Proj="FG防城港核电"; Unit="FG-1防城港1号机组"; Sys="AFW辅助给水系统"; Pen="AFW-PN001贯穿件AFW-PN001"; Valve="1AFW001VA隔离阀"
       Device="DEV-002"; Time="2026-07-11 08:00:00"; Rate="2.560"; Result="合格"; Pressure="0.395" },
    @{ Proj="FG防城港核电"; Unit="FG-1防城港1号机组"; Sys="AFW辅助给水系统"; Pen="AFW-PN001贯穿件AFW-PN001"; Valve="1AFW002VA隔离阀"
       Device="DEV-003"; Time="2026-07-11 15:00:00"; Rate="3.120"; Result="合格"; Pressure="0.398" },

    # === ZZ-2: 主蒸汽系统MSS ===
    @{ Proj="ZZ漳州核电"; Unit="ZZ-2漳州2号机组"; Sys="MSS主蒸汽系统"; Pen="MSS-PN001贯穿件MSS-PN001"; Valve="2MSS001VA隔离阀"
       Device="DEV-001"; Time="2026-07-12 09:30:00"; Rate="5.430"; Result="合格"; Pressure="0.445" },
    @{ Proj="ZZ漳州核电"; Unit="ZZ-2漳州2号机组"; Sys="MSS主蒸汽系统"; Pen="MSS-PN001贯穿件MSS-PN001"; Valve="2MSS002VA隔离阀"
       Device="DEV-002"; Time="2026-07-12 14:00:00"; Rate="9.120"; Result="不合格"; Pressure="0.448" },

    # === HN-3 复测: 安全壳系统CAM (对同一个阀门再次测试) ===
    @{ Proj="HN海南核电"; Unit="HN-3海南3号机组"; Sys="CAM安全壳系统"; Pen="PN219贯穿件PN219"; Valve="3CAM007VA隔离阀"
       Device="DEV-001"; Time="2026-07-15 09:00:00"; Rate="6.734"; Result="合格"; Pressure="0.421" },
    @{ Proj="HN海南核电"; Unit="HN-3海南3号机组"; Sys="CAM安全壳系统"; Pen="PN220贯穿件PN220"; Valve="3CAM008VA隔离阀"
       Device="DEV-003"; Time="2026-07-15 14:30:00"; Rate="6.436"; Result="合格"; Pressure="0.421" }
)

# ============================================================
# 生成过程曲线数据（模拟建压→稳压→采集过程）
# ============================================================
function New-CurveData {
    param([string]$TestTime, [double]$Pressure, [double]$LeakRate)

    $baseTime = [datetime]::Parse($TestTime)
    $lines = [System.Collections.Generic.List[string]]::new()
    # 客户「数据报表」过程数据格式：6 列，第一列为「时间」
    $lines.Add("时间,实时压力P1,瞬时流量M1,瞬时流量M2,温度T_R,压力P2_R")

    $baseTemp = 24.5
    for ($i = 0; $i -lt 60; $i++) {
        $t = $baseTime.AddSeconds($i * 10)
        $phase = $i / 60.0

        if ($phase -lt 0.15) {
            # 建压阶段
            $p = $Pressure * (1 - [Math]::Exp(-($phase / 0.15) * 4))
            $f = $LeakRate * (2 + (Get-Random -Maximum 100) / 100) * (1 - $phase)
        } elseif ($phase -lt 0.3) {
            # 稳压阶段
            $p = $Pressure * (1.05 - 0.05 * (($phase - 0.15) / 0.15))
            $f = $LeakRate * (1.5 + 0.5 * [Math]::Sin(($phase - 0.15) / 0.15 * 10))
        } else {
            # 采集阶段
            $p = $Pressure + (Get-Random -Minimum -50 -Maximum 50) / 10000
            $f = $LeakRate + 0.003 * [Math]::Sin(($phase - 0.3) / 0.7 * 20)
        }

        # 瞬时流量 M2：第二流量通道，取 M1 的 0.9 倍附近
        $f2 = $f * 0.9
        # 压力 P2_R：背压，取 P1 的 0.9 倍附近
        $p2 = $p * 0.9
        $temp = $baseTemp + 0.3 * $phase + (Get-Random -Minimum -10 -Maximum 10) / 100

        # 时间用客户斜杠风格（带秒以区分采样点），列序：时间,P1,M1,M2,T_R,P2_R
        $lines.Add("$($t.ToString('yyyy/M/d HH:mm:ss')),$([Math]::Round($p, 4)),$([Math]::Round($f, 4)),$([Math]::Round($f2, 4)),$([Math]::Round($temp, 2)),$([Math]::Round($p2, 4))")
    }

    return ($lines -join "`r`n")
}

# ============================================================
# 生成文件
# ============================================================
$count = 0
foreach ($t in $tests) {
    $valveCode = ($t.Valve -split '[^\x00-\x7F]')[0]  # 提取编码部分（ASCII前缀）
    # 更可靠的方式：用 SplitCodeName 的逻辑提取前导ASCII
    $valveCode = ""
    foreach ($c in $t.Valve.ToCharArray()) {
        if ($c -match '[A-Za-z0-9\-_]') { $valveCode += $c } else { break }
    }

    # 构建文件夹路径（用 Path.Combine 以兼容 PowerShell 5.1，Join-Path 5.1 不支持多段）
    $folder = [System.IO.Path]::Combine($root, $t.Proj, $t.Unit, $t.Sys, $t.Pen, $t.Valve)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null

    $timeFormatted = $t.Time -replace ' ', '_' -replace ':', ''
    $timeForFile = ($t.Time -replace ' ', '_') -replace ':', ''

    # GBK 编码（与客户「实验报表/数据报表」一致）
    $gbk = [System.Text.Encoding]::GetEncoding(936)

    # --- 结果 CSV（客户「实验报表」宽表格式，每阀门一行，新增「测量装置编号」列）---
    # 系统编码：取系统文件夹名的前导字母（如 CAM安全壳系统 → CAM）
    $sysCode = ""
    foreach ($c in $t.Sys.ToCharArray()) {
        if ($c -match '[A-Za-z0-9\-_]') { $sysCode += $c } else { break }
    }

    # 以下字段系统数据模型中没有，填充合理的模拟值
    $rateVal = [double]$t.Rate
    $pressVal = [double]$t.Pressure
    $penDia = [Math]::Round((Get-Random -Minimum 100 -Maximum 4000) / 10.0, 1)   # 贯穿件直径
    $nomDia = $penDia                                                            # 阀门公称直径
    if ($t.Result -eq "合格") { $designMax = [Math]::Round($rateVal * 1.3 + 1, 1) }
    else                       { $designMax = [Math]::Round($rateVal * 0.8, 1) }  # 泄漏率设计最大值（限值）
    $prechargeP2 = [Math]::Round($pressVal * 1.05, 3)                            # 预充压压力P2
    $testP2 = [Math]::Round($pressVal * 0.98, 3)                                 # 试验压力P2
    $testP1 = $pressVal                                                          # 试验压力P1（=试验压力）
    $reading = $t.Rate                                                           # 试验仪器读数（=最终泄漏率）
    $expDate = ([datetime]$t.Time).ToString('yyyy/M/d H:mm')                     # 实验日期（客户斜杠风格）

    $summaryHeader = "序号,系统,贯穿件直径,试验阀门编号,阀门公称直径,阀门泄漏率设计最大值,预充压压力P2,试验压力P2,试验压力P1,试验仪器读数,测量装置编号,实验日期,实验结果"
    $summaryRow = "1,$sysCode,$penDia,$valveCode,$nomDia,$designMax,$prechargeP2,$testP2,$testP1,$reading,$($t.Device),$expDate,$($t.Result)"
    $summaryContent = "$summaryHeader`r`n$summaryRow"
    $summaryFile = Join-Path $folder "${valveCode}_${timeForFile}_结果汇总.csv"
    [System.IO.File]::WriteAllText($summaryFile, $summaryContent, $gbk)

    # --- 过程数据 CSV（客户「数据报表」6列格式）---
    $curveContent = New-CurveData -TestTime $t.Time -Pressure $pressVal -LeakRate $rateVal
    $curveFile = Join-Path $folder "${valveCode}_${timeForFile}_过程数据.csv"
    [System.IO.File]::WriteAllText($curveFile, $curveContent, $gbk)

    $count++
    Write-Host "  [$count/$($tests.Count)] $valveCode -> $folder"
}

Write-Host ""
Write-Host "=== 测试数据生成完成 ==="
Write-Host "共生成 $count 组数据（$($count * 2) 个文件）"
Write-Host "文件夹位置: $root"
Write-Host ""
Write-Host "数据分布:"
Write-Host "  HN海南核电 / HN-3: 6条 (CAM系统, 5合格+0不合格)"
Write-Host "  HN海南核电 / HN-4: 2条 (CVS系统, 1合格+1不合格)"
Write-Host "  ZZ漳州核电 / ZZ-1: 2条 (RHR系统, 1合格+1不合格)"
Write-Host "  ZZ漳州核电 / ZZ-2: 2条 (MSS系统, 1合格+1不合格)"
Write-Host "  GD广东核电 / GD-1: 2条 (CDS系统, 2合格)"
Write-Host "  FG防城港核电 / FG-1: 2条 (AFW系统, 2合格)"
Write-Host ""
Write-Host "导入方法: 基础台账 → 项目机组管理 → 批量导入 → 选择 '$root' 文件夹"
