using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore;
using IsolationLeakage.App.Data;
using IsolationLeakage.App.Models;
using IsolationLeakage.App.Models.Database;

namespace IsolationLeakage.App.Services;

/// <summary>
/// 试验对象路径服务
/// </summary>
public sealed class TestObjectPathService
{
    private readonly AppDbContext _context;

    public TestObjectPathService(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 获取机组下的根节点（系统节点）
    /// </summary>
    public async Task<List<TestObjectPathNode>> GetRootNodesAsync(string unitCode)
    {
        return await _context.TestObjectPathNodes
            .Where(n => n.UnitCode == unitCode && n.ParentCode == null)
            .Include(n => n.Children)
            .OrderBy(n => n.Code)
            .ToListAsync();
    }

    /// <summary>
    /// 递归加载机组下的完整路径树
    /// </summary>
    public async Task<List<TestObjectPathNode>> GetTreeAsync(string unitCode)
    {
        var allNodes = await _context.TestObjectPathNodes
            .Where(n => n.UnitCode == unitCode)
            .Include(n => n.TestRecords)
            .ToListAsync();

        // 构建树形结构（内存中）
        var lookup = allNodes.ToLookup(n => n.ParentCode);
        foreach (var node in allNodes)
        {
            node.Children = new ObservableCollection<TestObjectPathNode>(lookup[node.Code]);
        }

        return lookup[null].OrderBy(n => n.Code).ToList();
    }

    /// <summary>
    /// 根据编号获取节点
    /// </summary>
    public async Task<TestObjectPathNode?> GetByCodeAsync(string code)
    {
        return await _context.TestObjectPathNodes
            .Include(n => n.Parent)
            .Include(n => n.Children)
            .Include(n => n.TestRecords)
            .FirstOrDefaultAsync(n => n.Code == code);
    }

    /// <summary>
    /// 添加节点
    /// </summary>
    public async Task<TestObjectPathNode> AddAsync(TestObjectPathNode node)
    {
        if (await _context.TestObjectPathNodes.AnyAsync(n => n.Code == node.Code))
        {
            throw new InvalidOperationException("节点编号已存在");
        }

        // 验证父节点存在
        if (!string.IsNullOrEmpty(node.ParentCode))
        {
            var parent = await GetByCodeAsync(node.ParentCode);
            if (parent == null)
            {
                throw new InvalidOperationException("父节点不存在");
            }

            // 验证层级关系：只能在系统下建贯穿件/阀门，只能在贯穿件下建阀门
            if (!CanCreateChildNode(parent.NodeType, node.NodeType))
            {
                throw new InvalidOperationException($"不能在 {GetNodeTypeName(parent.NodeType)} 下创建 {GetNodeTypeName(node.NodeType)}");
            }
        }

        _context.TestObjectPathNodes.Add(node);
        await _context.SaveChangesAsync();
        return node;
    }

    /// <summary>
    /// 更新节点
    /// </summary>
    public async Task UpdateAsync(TestObjectPathNode node)
    {
        node.UpdatedAt = DateTime.Now;
        _context.TestObjectPathNodes.Update(node);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 删除节点（删除保护：有历史数据或有子节点时不允许删除）
    /// </summary>
    public async Task<bool> DeleteAsync(string code)
    {
        var node = await GetByCodeAsync(code);
        if (node == null) return false;

        // 删除保护：有子节点时不允许删除
        if (node.Children.Any())
        {
            throw new InvalidOperationException("该节点下有子节点，不允许删除");
        }

        // 删除保护：有历史试验数据时不允许删除
        if (await _context.TestRecords.AnyAsync(r => r.ObjectCode == code))
        {
            throw new InvalidOperationException("该节点已有历史试验数据，不允许删除");
        }

        _context.TestObjectPathNodes.Remove(node);
        await _context.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// 搜索节点
    /// </summary>
    public async Task<List<TestObjectPathNode>> SearchAsync(string unitCode, string keyword)
    {
        keyword = keyword.Trim().ToLower();
        return await _context.TestObjectPathNodes
            .Where(n => n.UnitCode == unitCode &&
                        (n.Code.ToLower().Contains(keyword) ||
                         n.Name.ToLower().Contains(keyword)))
            .Include(n => n.Children)
            .OrderBy(n => n.Code)
            .ToListAsync();
    }

    /// <summary>
    /// 获取节点的试验记录统计
    /// </summary>
    public async Task<(int TotalTests, int FailedTests, DateTime? LastTestTime)> GetTestStatisticsAsync(string nodeCode)
    {
        var records = await _context.TestRecords
            .Where(r => r.ObjectCode == nodeCode)
            .ToListAsync();

        if (!records.Any())
        {
            return (0, 0, null);
        }

        return (
            records.Count,
            records.Count(r => r.Result == TestResult.Fail),
            records.Max(r => r.TestTime)
        );
    }

    /// <summary>
    /// 检查是否可以在父节点类型下创建子节点类型
    /// </summary>
    private bool CanCreateChildNode(PathNodeType parentType, PathNodeType childType)
    {
        return parentType switch
        {
            // 系统下可以建：贯穿件、阀门、其他部件
            PathNodeType.System => childType is PathNodeType.Penetration or PathNodeType.Valve or PathNodeType.OtherComponent,
            // 贯穿件下可以建：阀门、其他部件
            PathNodeType.Penetration => childType is PathNodeType.Valve or PathNodeType.OtherComponent,
            // 阀门和部件不能有子节点
            PathNodeType.Valve => false,
            PathNodeType.OtherComponent => false,
            _ => false
        };
    }

    private static string GetNodeTypeName(PathNodeType type)
    {
        return type switch
        {
            PathNodeType.System => "系统",
            PathNodeType.Penetration => "贯穿件",
            PathNodeType.Valve => "阀门",
            PathNodeType.OtherComponent => "其他部件",
            _ => "未知"
        };
    }
}
