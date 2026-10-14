# WorkBuddy：任务、专家、项目与产物

核查日期：2026-10-14。证据来自 WorkBuddy 中文官方使用文档；没有安装客户端、登录账号或实际运行任务。下文产品行为均为**官方声明**，不等于源码确认或实测。个人桌面产品、CodeBuddy CLI、WorkBuddy Enterprise / Managed Agents 不混为同一实现。

## 产品为什么与本需求直接相关

WorkBuddy 值得参考的重点是让用户交付办公成果：用自然语言启动任务，在结果区查看、修改和继续使用生成文件。第一任务文档把需求输入、执行过程和成果查看连在一起。[开启第一个任务](https://www.workbuddy.cn/docs/workbuddy/FirstTask)

这与本项目的办公、研发、经营三种工作模式相符。我们的差异化假设应放在本地可携带数据、长期岗位责任和可核对的自动执行上；是否形成付费价值仍需真实用户验证。

## 路径一：任务 → 专家或专家团 → 交付

官方明确区分：专家提供角色、方法和工具组合；专家团由团长拆解、分配并汇总任务。创建专家可以通过引导对话完成。专家本身不获得系统权限；所需服务未连接时，任务才显示连接卡片，授权后继续。[专家](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Expert-Center)

**设计判断：** “岗位身份”“本次组队”“工具授权”应是三个对象。首页不要求先建完整组织，用户可直接交办；重复出现的工作方法再保存为岗位模板。依赖缺失在使用时解决，避免首次设置一长串 API。

**未证实：** 专家团的内部进程隔离、队列持久化、重启后的子任务归属、共享文件冲突策略。文档说明自动协作，不能替代这些技术证据。

## 路径二：项目共享 → 任务转交 → 沉淀资料

项目统一提供指令、专家、技能、连接器和资料；任务对应会话与工作空间。转交可携带产物与上下文摘要，资料库通过 RAG 供后续任务使用。项目级 `.codebuddy` 配置可覆盖同名用户配置。云项目资产依赖腾讯侧存储，不能据“工作空间”推断全部数据仅在本机。[项目](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Project)

**设计判断：** 本项目可以让同一项目的岗位共享业务说明和经过确认的资料；任务转派必须带输入、当前结果、阻塞项和下一步。共享产物不是自动把所有私有对话注入所有岗位。文件产出后还应有验收状态，而不是以模型停止输出代表完成。

资料库的协作说明涉及人和 Agent 的文档共创，Markdown 修改有建议与接受过程；它不直接证明长期岗位会自行领取企业待办。[多人多 Agent 协作](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Library/Collaboration)

## 模型、技能、记忆与自动化

| 领域 | 官方可确认的产品表达 | 还不能据此认定 |
| --- | --- | --- |
| 模型 | 有可视化自定义提供商配置；也用速度/能力档位降低选择负担。[模型配置](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Model) | 任意兼容端点都支持工具、流式和所有多模态能力 |
| 技能 | 技能安装、启停与技能文件本身是不同操作。[技能](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Skills-Market) | 所有平台的插件包能原样执行，或停用等同于卸载 |
| 记忆 | 定期从会话提取事实、偏好和跟进事项；用户可查看、更正、删除、关闭与导入，并用于上下文和历史检索。[记忆](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Memory) | 已具备我们的岗位经验发布、冷热归档、证据溯源及冲突合并模型 |
| 自动化 | 按时间执行 prompt，携带工作空间和能力配置，可推送结果；通知失败不必使任务失败，推送可能使用云端存储。[自动化](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Automation-Guide) | 邮件或外部动作具备恰好一次投递；桌面离线后所有任务仍会运行 |

记忆的编辑、关闭和删除应出现在用户能找到的位置；生成过的结果与后续记忆使用是两回事，删除记忆不能让旧产物自动消失。本文没有验证其夜间提取、云端存储或删除的实际行为。

## 授权和恢复的证据边界

官方默认权限方案允许工作空间内常见操作自动进行，高风险动作另行确认，并说明备份或回收站机制。完全访问权限的影响范围较大，文档说明其在当前客户端全局生效；不宜直接作为本项目“某条固定流程已授权”的替代品。[默认权限与安全沙箱](https://www.workbuddy.cn/docs/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/Permission-Modes)

更新日志持续提到会话恢复、产物保留、权限等待、子任务和连接器故障修复。它说明这些场景确实需要验收，但不应把“列出修复”当作该版本在我们设备上已经通过。[更新日志](https://www.workbuddy.cn/docs/workbuddy/Changelog)

## 建议借鉴与不照搬

建议借鉴：任务优先入口；角色与协作组分离；缺少连接时就地补齐；项目共享资料；产物和上下文一同转交；可编辑的记忆。

不照搬：先搭企业成员和审批体系才能开工；用全局无限制权限换取某个例行任务自动执行；把云项目资源包装成本地唯一事实源。这里是针对 OPC、本地部署要求的取舍。

下一轮试用应记录：首次任务要填多少项配置；专家团是否比单专家提高成果质量；断网和重启是否保住待确认内容；转交后是否能继续编辑原产物；删除记忆后新任务是否还引用它；定时任务漏跑和通知失败如何提示。本次没有这些实测结果。
