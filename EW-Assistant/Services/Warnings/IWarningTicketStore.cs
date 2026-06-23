using System.Collections.Generic;

namespace EW_Assistant.Warnings
{
    /// <summary>
    /// 预警工单存储接口。
    /// </summary>
    public interface IWarningTicketStore
    {
        /// <summary>加载全部工单记录（可返回空列表，不抛异常）。</summary>
        IList<WarningTicketRecord> LoadAll();
        /// <summary>用给定列表覆盖存储，调用方负责传入去重/去空后的集合。</summary>
        void SaveAll(IList<WarningTicketRecord> tickets);
    }
}
