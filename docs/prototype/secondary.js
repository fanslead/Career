(function (root) {
  "use strict";

  var modelSecretDraft = "";
  var retrievalSecretDraft = "";
  var smtpSecretDraft = "";
  var pendingEmailRecipient = "";
  var serial = 1;
  var readOnlyActions = {
    "sec-team-select": true,
    "sec-team-new": true,
    "sec-team-edit": true,
    "sec-team-cancel": true,
    "sec-team-archive": true,
    "sec-library-tab": true,
    "sec-knowledge-citation": true,
    "sec-memory-select": true,
    "sec-memory-edit": true,
    "sec-memory-cancel": true,
    "sec-memory-share-preview": true,
    "sec-memory-forget": true,
    "sec-memory-candidate-preview": true,
    "sec-index-open": true,
    "sec-settings-tab": true,
    "sec-email-send-confirm": true,
    "sec-backup-restore-open": true,
    "sec-backup-diagnostics-preview": true,
    "sec-access-revoke-open": true,
    "sec-close-modal": true
  };

  var settingTabs = [
    ["models", "模型"],
    ["tools", "工具与插件"],
    ["email", "邮箱"],
    ["budget", "预算"],
    ["automation", "自动化"],
    ["backup", "工作区备份"],
    ["access", "账号与访问"]
  ];

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function e(U, value) {
    return U && typeof U.esc === "function" ? U.esc(value == null ? "" : String(value)) : escapeHtml(value);
  }

  function attrs(U, values) {
    var result = "";
    Object.keys(values || {}).forEach(function (key) {
      result += " " + key + "='" + e(U, values[key]) + "'";
    });
    return result;
  }

  function button(U, label, action, variant, extra) {
    var known = { primary: true, danger: true, quiet: true };
    var safeVariant = known[variant] ? " " + variant : "";
    var extraClass = extra && extra.class ? " " + extra.class : "";
    var className = "btn" + safeVariant + extraClass;
    var htmlAttrs = {};
    Object.keys(extra || {}).forEach(function (key) {
      if (key !== "class") htmlAttrs[key] = extra[key];
    });
    if (readOnlyActions[action]) htmlAttrs["data-readonly"] = "true";
    if (!extra && !readOnlyActions[action] && U && typeof U.button === "function") return U.button(label, action, variant || "");
    return "<button type='button' class='" + e(U, className) + "' data-action='" + e(U, action) + "'" + attrs(U, htmlAttrs) + ">" + e(U, label) + "</button>";
  }

  function badge(U, text, tone) {
    return "<span class='badge " + e(U, tone || "neutral") + "'>" + e(U, text) + "</span>";
  }

  function field(U, label, html) {
    var labeledHtml = ensureControlIds(html);
    if (U && typeof U.field === "function") return U.field(label, labeledHtml);
    return "<label class='field'><span>" + e(U, label) + "</span>" + labeledHtml + "</label>";
  }

  function selected(value, expected) {
    return String(value) === String(expected) ? " selected" : "";
  }

  function checked(value) {
    return value ? " checked" : "";
  }

  function valueInput(name, value, type, options) {
    var extra = options || "";
    return "<input class='input' id='" + escapeHtml(name) + "' type='" + (type || "text") + "' name='" + name + "' value='" + escapeHtml(value) + "'" + extra + ">";
  }

  function textArea(name, value, rows) {
    return "<textarea class='input' id='" + escapeHtml(name) + "' name='" + name + "' rows='" + (rows || 3) + "'>" + escapeHtml(value) + "</textarea>";
  }

  function selectInput(name, options, current) {
    return "<select class='input' id='" + escapeHtml(name) + "' name='" + name + "'>" + options.map(function (item) {
      return "<option value='" + escapeHtml(item[0]) + "'" + selected(current, item[0]) + ">" + escapeHtml(item[1]) + "</option>";
    }).join("") + "</select>";
  }

  function ensureControlIds(html) {
    return String(html).replace(/<(input|select|textarea)\b([^>]*)>/gi, function (whole, tagName, attributes) {
      var name = attributes.match(/\bname\s*=\s*(['"])(.*?)\1/i);
      if (!name) return whole;
      var id = attributes.match(/\bid\s*=\s*(['"])(.*?)\1/i);
      if (id) {
        if (id[2] === name[2]) return whole;
        return "<" + tagName + attributes.replace(id[0], "id='" + escapeHtml(name[2]) + "'") + ">";
      }
      return "<" + tagName + " id='" + escapeHtml(name[2]) + "'" + attributes + ">";
    });
  }

  function note(text, tone) {
    return "<div class='notice " + (tone || "warn") + "'>" + text + "</div>";
  }

  function escList(U, list) {
    return (list || []).map(function (item) { return e(U, item); }).join("、");
  }

  function init(S) {
    if (!S.secondary || typeof S.secondary !== "object" || Object.keys(S.secondary).length === 0) {
      S.secondary = {
        team: {
          mode: "detail",
          selectedId: "member-lin",
          editingId: null,
          draft: null,
          handoverDraft: null,
          pendingArchiveId: null,
          members: [
            {
              id: "member-lin",
              name: "林知",
              role: "研究分析员",
              status: "active",
              accepting: true,
              activeTasks: ["整理秋季产品调研，形成一份决策简报"],
              responsibilities: ["秋季产品调研", "验证结论与证据边界"],
              capabilities: ["资料检索", "竞品分析", "决策简报"],
              recent: "12 分钟前"
            },
            {
              id: "member-zhou",
              name: "周远",
              role: "研发",
              status: "active",
              accepting: true,
              activeTasks: ["秋季产品验证：复核功能清单"],
              responsibilities: ["产品功能验证", "技术风险复核"],
              capabilities: ["研发分析", "缺陷归纳", "版本检查"],
              recent: "31 分钟前"
            },
            {
              id: "member-xu",
              name: "陈晓",
              role: "客户跟进",
              status: "active",
              accepting: true,
              activeTasks: ["秋季产品验证：整理试用反馈"],
              responsibilities: ["客户回访", "试用反馈跟进"],
              capabilities: ["客户沟通", "问题归类", "反馈汇总"],
              recent: "昨天"
            }
          ],
          handovers: []
        },
        library: {
          tab: "knowledge",
          selectedDocId: "doc-handbook",
          selectedMemoryId: "memory-research",
          editingMemoryId: null,
          memoryDraft: null,
          pendingForgetId: null,
          forgetDraft: null,
          selectedFileName: "",
          index: {
            currentGeneration: "index-2026-10-13",
            candidateGeneration: "",
            rebuildStatus: "idle",
            previousGeneration: "",
            candidateDraft: "",
            candidateDraftSet: false
          },
          candidates: [
            {
              id: "candidate-feedback",
              title: "试用反馈中的优先验证项",
              kind: "experience",
              scopeType: "project",
              scopeLabel: "项目：秋季产品验证",
              source: "用户访谈汇总 · 已验收版本 2",
              sourceRef: "TASK-APR-01 · Run 3",
              content: "把重复出现的试用阻碍优先列入下一轮验证；单条反馈仍需保留来源和样本范围。"
            }
          ],
          pendingCandidateId: null,
          documents: [
            {
              id: "doc-handbook",
              name: "秋季产品调研摘要.md",
              scope: "项目：秋季产品验证",
              version: 3,
              status: "indexed",
              pages: 18,
              chunks: 42,
              citation: { page: 2, paragraph: "2.1", text: "本次产品验证应区分用户明确反馈、观察到的使用行为和团队推断，并为每项结论保留来源。" },
              updated: "今天 09:42"
            },
            {
              id: "doc-failed",
              name: "旧版产品介绍.pdf",
              scope: "工作区",
              version: 1,
              status: "failed",
              error: "文件加密，无法读取文本层。可更换可访问版本后重试。",
              pages: 0,
              chunks: 0,
              citation: null,
              updated: "今天 08:16"
            }
          ],
          memories: [
            {
              id: "memory-research",
              title: "决策简报要区分反馈与推断",
              kind: "experience",
              scopeType: "member",
              scopeLabel: "林知（私有）",
              source: "任务：秋季产品验证 · 整理秋季产品调研，形成一份决策简报 · 已验收版本 2",
              sourceRef: "TASK-APR-01 · Run 3",
              version: 2,
              status: "Active",
              recall: "Warm",
              content: "整理秋季产品调研时，先区分客户明确反馈、观察到的使用行为与团队推断。只有证据支持时，才将观察写入决策建议。",
              uses: 4,
              usedIn: ["秋季产品决策简报 v2", "秋季产品验证记录 v1"],
              updated: "今天 10:05",
              history: []
            },
            {
              id: "memory-tone",
              title: "面向客户的说明使用短句",
              kind: "fact",
              scopeType: "project",
              scopeLabel: "项目：秋季产品验证",
              source: "用户确认 · 项目偏好",
              sourceRef: "项目设置 · revision 5",
              version: 1,
              status: "Active",
              recall: "Hot",
              content: "客户可见说明使用简短句子，先说结论，再列出需要对方确认的内容。",
              uses: 7,
              usedIn: ["客户跟进邮件草稿 v1", "项目周报 v3"],
              updated: "昨天",
              history: []
            }
          ],
          forgetLog: []
        },
        settings: {
          tab: "models",
          drafts: {},
          model: {
            protocol: "responses",
            baseUrl: "https://api.openai.com/v1",
            model: "gpt-4.1",
            keyConfigured: false,
            result: "尚未测试",
            testedAt: ""
          },
          retrieval: {
            baseUrl: "https://api.openai.com/v1",
            model: "text-embedding-3-small",
            dimensions: "1536",
            keyConfigured: false,
            result: "尚未测试",
            generation: "index-2026-10-13"
          },
          tools: [
            { id: "files", name: "限定目录文件", type: "内置工具", installed: true, authorized: true, scope: "current-task", member: "默认助理" },
            { id: "browser", name: "网页浏览", type: "内置工具", installed: true, authorized: false, scope: "current-task", member: "默认助理" },
            { id: "calendar-mcp", name: "日历 MCP 连接", type: "工具连接", installed: false, authorized: false, scope: "current-project", member: "默认助理" },
            { id: "research-skill", name: "资料研究技能包", type: "技能包", installed: false, authorized: false, scope: "current-member", member: "林知" }
          ],
          email: {
            host: "smtp.example.test",
            port: "587",
            encryption: "starttls",
            account: "reports@example.test",
            senderName: "Career 工作区",
            from: "reports@example.test",
            to: "owner@example.test",
            replyTo: "",
            keyConfigured: false,
            result: "尚未测试",
            lastPreview: "",
            contacts: [
              { id: "contact-owner", name: "工作区负责人", email: "owner@example.test" },
              { id: "contact-research", name: "研究收件箱", email: "research@example.test" }
            ],
            frequency: "weekly",
            quietStart: "22:00",
            quietEnd: "08:00"
          },
          budget: {
            monthly: "120",
            tokens: "250000",
            calls: "800",
            concurrency: "2",
            duration: "30"
          },
          automation: {
            enabled: true,
            schedule: "09:00",
            timezone: "Asia/Shanghai",
            missed: "merge",
            catchUp: "1",
            next: "2026-10-15 09:00 · Asia/Shanghai",
            lastRun: "上次按时运行：2026-10-14 09:00"
          },
          backup: {
            workspace: "/Users/me/Career-workspace",
            schedule: "daily",
            retention: "14",
            restoreDirectoryDraft: "",
            restoreDirectoryDraftSet: false,
            backups: [
              { id: "backup-good", name: "自动备份 · 2026-10-13 20:00", keyAvailable: true, status: "可恢复", path: "/backups/2026-10-13" },
              { id: "backup-keyless", name: "迁移备份 · 2026-09-30 18:20", keyAvailable: false, status: "缺少恢复密钥", path: "/backups/2026-09-30" }
            ],
            lastRestore: ""
          },
          access: {
            paired: true,
            loggedIn: true,
            pairingCode: "",
            pairingExpiry: "",
            pairingCodeInput: "",
            credentials: [],
            webhook: { enabled: false, scope: "current-project", status: "未启用" },
            sessions: [
              { id: "session-current", name: "当前浏览器", device: "本机浏览器", current: true, status: "有效", lastSeen: "刚刚" },
              { id: "session-laptop", name: "备用笔记本", device: "Firefox · macOS", current: false, status: "有效", lastSeen: "昨天 18:42" }
            ],
            pendingRevokeId: null
          }
        }
      };
    }
    ensureSecondaryFields(S.secondary);
    return S.secondary;
  }

  function ensureSecondaryFields(state) {
    state.team = state.team || {};
    state.team.members = Array.isArray(state.team.members) ? state.team.members : [];
    state.team.handovers = Array.isArray(state.team.handovers) ? state.team.handovers : [];
    if (state.team.mode == null) state.team.mode = "detail";
    if (state.team.selectedId == null) state.team.selectedId = state.team.members[0] ? state.team.members[0].id : null;
    state.team.members.forEach(function (member) {
      if (!Array.isArray(member.activeTasks)) member.activeTasks = [];
      if (!Array.isArray(member.responsibilities)) member.responsibilities = [];
      if (!Array.isArray(member.capabilities)) member.capabilities = [];
      if (!member.status) member.status = "active";
      if (member.accepting == null) member.accepting = true;
    });

    state.library = state.library || {};
    var library = state.library;
    if (!library.index || typeof library.index !== "object") library.index = {};
    if (!library.index.currentGeneration) library.index.currentGeneration = "index-2026-10-13";
    if (!library.index.candidateGeneration) library.index.candidateGeneration = "";
    if (!library.index.rebuildStatus) library.index.rebuildStatus = "idle";
    if (!library.index.previousGeneration) library.index.previousGeneration = "";
    if (library.index.candidateDraft == null) library.index.candidateDraft = "";
    if (library.index.candidateDraftSet == null) library.index.candidateDraftSet = !!library.index.candidateDraft;
    if (!Array.isArray(library.candidates)) library.candidates = [{
      id: "candidate-feedback",
      title: "试用反馈中的优先验证项",
      kind: "experience",
      scopeType: "project",
      scopeLabel: "项目：秋季产品验证",
      source: "用户访谈汇总 · 已验收版本 2",
      sourceRef: "TASK-APR-01 · Run 3",
      content: "把重复出现的试用阻碍优先列入下一轮验证；单条反馈仍需保留来源和样本范围。"
    }];
    if (!Array.isArray(library.documents)) library.documents = [];
    if (!Array.isArray(library.memories)) library.memories = [];
    if (!Array.isArray(library.forgetLog)) library.forgetLog = [];
    if (!Array.isArray(library.forgetDraft)) library.forgetDraft = null;
    if (library.pendingCandidateId === undefined) library.pendingCandidateId = null;
    if (library.selectedFileName === undefined) library.selectedFileName = "";
    if (library.tab !== "memory" && library.tab !== "knowledge") library.tab = "knowledge";

    state.settings = state.settings || {};
    var settings = state.settings;
    if (!settings.drafts || typeof settings.drafts !== "object") settings.drafts = {};
    settings.email = settings.email || {};
    if (settings.email.host == null) settings.email.host = "smtp.example.test";
    if (settings.email.port == null) settings.email.port = "587";
    if (settings.email.encryption == null) settings.email.encryption = "starttls";
    if (settings.email.account == null) settings.email.account = "reports@example.test";
    if (settings.email.senderName == null) settings.email.senderName = "Career 工作区";
    if (settings.email.from == null) settings.email.from = "reports@example.test";
    if (settings.email.to == null) settings.email.to = "owner@example.test";
    if (settings.email.replyTo == null) settings.email.replyTo = "";
    if (settings.email.keyConfigured == null) settings.email.keyConfigured = false;
    if (!settings.email.result) settings.email.result = "尚未测试";
    if (settings.email.lastPreview == null) settings.email.lastPreview = "";
    if (!Array.isArray(settings.email.contacts)) settings.email.contacts = [
      { id: "contact-owner", name: "工作区负责人", email: "owner@example.test" },
      { id: "contact-research", name: "研究收件箱", email: "research@example.test" }
    ];
    if (!settings.email.frequency) settings.email.frequency = "weekly";
    if (!settings.email.quietStart) settings.email.quietStart = "22:00";
    if (!settings.email.quietEnd) settings.email.quietEnd = "08:00";
    settings.backup = settings.backup || {};
    if (!settings.backup.workspace) settings.backup.workspace = "/Users/me/Career-workspace";
    if (!settings.backup.schedule) settings.backup.schedule = "daily";
    if (!settings.backup.retention) settings.backup.retention = "14";
    if (settings.backup.restoreDirectoryDraft === undefined) settings.backup.restoreDirectoryDraft = "";
    if (settings.backup.restoreDirectoryDraftSet == null) settings.backup.restoreDirectoryDraftSet = !!settings.backup.restoreDirectoryDraft;
    if (!Array.isArray(settings.backup.backups)) settings.backup.backups = [];
    if (settings.backup.lastRestore == null) settings.backup.lastRestore = "";
    settings.access = settings.access || {};
    if (settings.access.paired == null) settings.access.paired = false;
    if (settings.access.loggedIn == null) settings.access.loggedIn = false;
    if (settings.access.pairingCode == null) settings.access.pairingCode = "";
    if (settings.access.pairingExpiry == null) settings.access.pairingExpiry = "";
    delete settings.access.pairingCodeInput;
    if (!Array.isArray(settings.access.credentials)) settings.access.credentials = [];
    if (!settings.access.webhook || typeof settings.access.webhook !== "object") {
      settings.access.webhook = { enabled: false, scope: "current-project", status: "未启用" };
    } else {
      if (settings.access.webhook.enabled == null) settings.access.webhook.enabled = false;
      if (!settings.access.webhook.scope) settings.access.webhook.scope = "current-project";
      if (!settings.access.webhook.status) settings.access.webhook.status = settings.access.webhook.enabled ? "已启用（原型模拟）" : "未启用";
    }
    if (settings.access.recoverySessions == null) settings.access.recoverySessions = 0;
    if (!Array.isArray(settings.access.sessions)) settings.access.sessions = [];
    if (settings.access.pendingRevokeId === undefined) settings.access.pendingRevokeId = null;
  }

  function commit(S, U, message, shouldRender) {
    if (U && typeof U.save === "function") U.save();
    if (shouldRender !== false && U && typeof U.render === "function") U.render();
    if (message && U && typeof U.toast === "function") U.toast(message);
  }

  function toast(U, message) {
    if (U && typeof U.toast === "function") U.toast(message);
  }

  function closeModal(U) {
    if (U && typeof U.closeModal === "function") U.closeModal();
  }

  function modal(U, title, body, footer) {
    if (U && typeof U.modal === "function") U.modal(title, body, footer);
  }

  function confirm(U, title, body, confirmLabel, action, variant) {
    if (U && typeof U.confirm === "function") U.confirm(title, body, confirmLabel, action, variant || "primary");
  }

  function inputValue(buttonElement, name) {
    if (typeof document === "undefined") return "";
    var scope = buttonElement && buttonElement.closest ? buttonElement.closest("[data-sec-form], [data-toolrow], dialog") : null;
    var element = scope ? scope.querySelector("[name='" + name + "']") : null;
    if (!element) element = document.querySelector("[name='" + name + "']");
    return element ? element.value : "";
  }

  function inputChecked(buttonElement, selector) {
    if (typeof document === "undefined") return false;
    var scope = buttonElement && buttonElement.closest ? buttonElement.closest("[data-sec-form], [data-toolrow], dialog") : null;
    var element = scope ? scope.querySelector(selector) : null;
    if (!element) element = document.querySelector(selector);
    return !!(element && element.checked);
  }

  function setInputValue(buttonElement, name, value) {
    if (typeof document === "undefined") return;
    var scope = buttonElement && buttonElement.closest ? buttonElement.closest("[data-sec-form], [data-toolrow], dialog") : null;
    var element = scope ? scope.querySelector("[name='" + name + "']") : null;
    if (!element) element = document.querySelector("[name='" + name + "']");
    if (element) element.value = value == null ? "" : String(value);
  }

  function fileName(buttonElement, name) {
    if (typeof document === "undefined") return "";
    var scope = buttonElement && buttonElement.closest ? buttonElement.closest("[data-sec-form]") : null;
    var element = scope ? scope.querySelector("[name='" + name + "']") : null;
    if (!element) element = document.querySelector("[name='" + name + "']");
    return element && element.files && element.files[0] ? element.files[0].name : "";
  }

  function valueFrom(buttonElement, name, fallback) {
    if (typeof document === "undefined") return fallback == null ? "" : fallback;
    var scope = buttonElement && buttonElement.closest ? buttonElement.closest("[data-sec-form], [data-toolrow], dialog") : null;
    var element = scope ? scope.querySelector("[name='" + name + "']") : null;
    if (!element) element = document.querySelector("[name='" + name + "']");
    return element ? element.value : (fallback == null ? "" : fallback);
  }

  function listFrom(text) {
    return String(text || "").split(/[\n,，]/).map(function (part) { return part.trim(); }).filter(Boolean);
  }

  function captureTeamDraft(team, buttonElement) {
    if (team.mode !== "edit") return;
    team.draft = {
      id: team.editingId || null,
      name: inputValue(buttonElement, "memberName"),
      role: inputValue(buttonElement, "memberRole"),
      responsibilities: inputValue(buttonElement, "memberResponsibilities"),
      capabilities: inputValue(buttonElement, "memberCapabilities")
    };
  }

  function captureMemoryDraft(library, buttonElement) {
    if (!library.editingMemoryId) return;
    library.memoryDraft = {
      id: library.editingMemoryId,
      content: inputValue(buttonElement, "memoryText")
    };
  }

  function memberById(team, id) {
    return team.members.find(function (item) { return item.id === id; });
  }

  function documentById(library, id) {
    return library.documents.find(function (item) { return item.id === id; });
  }

  function memoryById(library, id) {
    return library.memories.find(function (item) { return item.id === id; });
  }

  function syncTeamTasks(team, S) {
    if (!S || !S.tasks || typeof S.tasks !== "object") return;
    var tasks = Object.keys(S.tasks).map(function (key) { return S.tasks[key]; }).filter(Boolean);
    team.members.forEach(function (member) {
      member.activeTasks = tasks.filter(function (task) {
        return task.owner === member.name && task.phase !== "done" && task.phase !== "stopped" &&
          task.phase !== "draft" && task.phase !== "queued";
      }).map(function (task) { return task.title || task.name || "未命名任务"; });
    });
  }

  function memoryStatusLabel(status) {
    return ({ Active: "有效", Superseded: "已替代", Revoked: "已撤回", Deleted: "已遗忘" })[status] || status || "未知";
  }

  function memoryKindLabel(kind) {
    return ({ experience: "经验", fact: "事实" })[kind] || kind || "记忆";
  }

  function permissionTarget(S, scope) {
    if (scope === "current-member") return S.task && S.task.owner || "默认助理";
    if (scope === "current-project") return S.projects && S.projects[S.projectIndex || 0] && S.projects[S.projectIndex || 0].name || S.task && S.task.project || "尚未选择项目";
    return S.task ? (S.task.id || "草稿") + " · " + S.task.title : "尚未选择任务";
  }

  function permissionScopeLabel(scope) {
    return ({ "current-task": "仅当前任务", "current-member": "当前成员", "current-project": "当前项目" })[scope] || "当前任务";
  }

  function toolById(settings, id) {
    return settings.tools.find(function (item) { return item.id === id; });
  }

  function memberMarkup(U, member, selectedId) {
    var status = member.status === "archived" ? badge(U, "已归档", "neutral") :
      (member.accepting ? badge(U, "接单中", "good") : badge(U, "暂停接单", "warn"));
    return "<button type='button' class='list-row" + (member.id === selectedId ? " active" : "") + "' data-action='sec-team-select' data-readonly='true' data-id='" + e(U, member.id) + "'>" +
      "<span class='avatar'>" + e(U, member.name.slice(0, 1)) + "</span><span class='stack'><strong>" + e(U, member.name) + "</strong><span class='muted'>" + e(U, member.role) + " · " + member.activeTasks.length + " 项进行中</span></span><span class='wrap'>" + status + "</span></button>";
  }

  function teamEditor(U, state, member) {
    var isNew = !member;
    var currentId = member ? member.id : null;
    var draft = state.draft && state.draft.id === currentId ? state.draft : null;
    var title = isNew ? "新增 AI 成员" : "编辑成员职责与能力";
    var name = draft ? draft.name : member ? member.name : "";
    var role = draft ? draft.role : member ? member.role : "";
    var responsibilities = draft ? draft.responsibilities : member ? member.responsibilities.join("\n") : "";
    var capabilities = draft ? draft.capabilities : member ? member.capabilities.join("\n") : "";
    var html = "<div class='card stack' data-sec-form='team'>" +
      "<div class='section-head'><div><h2>" + e(U, title) + "</h2><p class='muted'>成员状态与活跃任务会显示在团队列表中。</p></div></div>" +
      field(U, "成员姓名", "<input class='input' name='memberName' value='" + e(U, name) + "' required>") +
      field(U, "岗位", "<input class='input' name='memberRole' value='" + e(U, role) + "' required>") +
      field(U, "职责（每行一项）", textArea("memberResponsibilities", responsibilities, 3)) +
      field(U, "能力（每行一项）", textArea("memberCapabilities", capabilities, 3)) +
      "<div class='row wrap'>" + button(U, "保存成员", "sec-team-save", "primary") + button(U, "取消", "sec-team-cancel", "quiet") + "</div></div>";
    return html;
  }

  function teamDetail(U, team, member) {
    if (!member) return "<div class='card empty'>选择一位成员查看详情。</div>";
    if (team.mode === "edit") return teamEditor(U, team, team.editingId ? member : null);
    var tasks = member.activeTasks.length ?
      "<ul>" + member.activeTasks.map(function (task) { return "<li>" + e(U, task) + "</li>"; }).join("") + "</ul>" :
      "<p class='muted'>当前没有活跃任务。</p>";
    var buttons = "<div class='row wrap'>" +
      button(U, "编辑职责与能力", "sec-team-edit", "", { "data-id": member.id }) +
      button(U, member.accepting ? "暂停接单" : "恢复接单", "sec-team-pause", member.accepting ? "quiet" : "primary", { "data-id": member.id }) +
      button(U, "归档成员", "sec-team-archive", "danger", { "data-id": member.id }) +
      "</div>";
    return "<div class='card stack'><div class='between wrap'><div><h2>" + e(U, member.name) + "</h2><p class='muted'>" + e(U, member.role) + "</p></div>" +
      (member.status === "archived" ? badge(U, "已归档", "neutral") : (member.accepting ? badge(U, "接单中", "good") : badge(U, "暂停接单", "warn"))) + "</div>" +
      (member.status === "archived" ? note("成员已归档，不再接收新任务。过往任务交接记录仍保留。", "warn") : "") +
      "<div class='detail-grid'><section><h3>职责</h3><p>" + escList(U, member.responsibilities) + "</p></section><section><h3>能力</h3><p>" + escList(U, member.capabilities) + "</p></section></div>" +
      "<div class='section-head'><h3>当前任务</h3><span class='muted'>最近活动：" + e(U, member.recent) + "</span></div>" + tasks +
      (team.handovers.length ? "<div class='divider'></div><h3>最近交接</h3><ul>" + team.handovers.slice(-3).map(function (handover) {
        return "<li>" + e(U, handover.tasks.join("、")) + " → " + e(U, handover.toName) + "</li>";
      }).join("") + "</ul>" : "") + buttons + "</div>";
  }

  function renderTeam(U, state, S) {
    var team = state.team;
    syncTeamTasks(team, S);
    var member = memberById(team, team.selectedId);
    var activeCount = team.members.filter(function (item) { return item.status === "active"; }).length;
    return "<header class='page-head'><div><div class='eyebrow'>协作空间</div><h1 id='page-title' tabindex='-1'>团队</h1><p class='muted'>管理 AI 成员、职责和接单状态。</p></div>" +
      button(U, "新增成员", "sec-team-new", "primary") + "</header>" +
      note("原型模拟：成员和任务状态只保存在本地演示数据中，不会启动真实 Agent。", "warn") +
      "<div class='grid-2'><section class='card stack'><div class='between'><h2>成员列表</h2><span class='muted'>" + activeCount + " 位在岗</span></div>" +
      "<div class='stack'>" + team.members.map(function (item) { return memberMarkup(U, item, team.selectedId); }).join("") + "</div></section>" +
      "<section>" + teamDetail(U, team, member) + "</section></div>";
  }

  function knowledgeTab(U, library) {
    var rows = library.documents.map(function (doc) {
      var status = doc.status === "indexed" ? badge(U, "可检索", "good") : doc.status === "processing" ? badge(U, "处理中", "warn") : badge(U, "解析失败", "bad");
      return "<div class='list-row'><span class='stack'><strong>" + e(U, doc.name) + "</strong><span class='muted'>" + e(U, doc.scope) + " · v" + e(U, doc.version) + " · " + e(U, doc.updated) + "</span></span><span class='wrap'>" + status +
        (doc.status === "indexed" ? badge(U, doc.chunks + " 个片段", "neutral") : "") + "</span><span class='row wrap'>" +
        button(U, "引用预览", "sec-knowledge-citation", "quiet", { "data-id": doc.id }) +
        (doc.status === "failed" ? button(U, "重试解析", "sec-knowledge-retry", "", { "data-id": doc.id }) : "") +
        "</span></div>" +
        (doc.status === "failed" ? note("失败原因：" + e(U, doc.error), "bad") : "");
    }).join("");
    return "<div class='card stack'><div class='section-head'><div><h2>知识库</h2><p class='muted'>原件、解析进度、索引版本与可定位引用。</p></div>" +
      button(U, "管理索引代次", "sec-index-open", "quiet") + "</div>" +
      "<div class='card stack' data-sec-form='knowledge'>" + field(U, "选择资料文件", "<input class='input' type='file' name='knowledgeFile' accept='.md,.txt,.pdf,.docx,.xlsx'>") +
      (library.selectedFileName ? "<p class='mini muted'>已选择（演示）： " + e(U, library.selectedFileName) + "</p>" : "") +
      "<p class='mini muted'>原型模拟：只读取文件名并登记一次解析失败，不上传或读取文件内容。</p>" +
      button(U, "导入并模拟解析失败", "sec-knowledge-import", "primary") + "</div>" +
      "<div class='stack'>" + rows + "</div></div>";
  }

  function memoryList(U, library) {
    return "<div class='stack'>" + library.memories.map(function (memory) {
      var isSelected = library.selectedMemoryId === memory.id;
      var title = memory.status === "Deleted" ? "已遗忘的记忆条目" : memory.title;
      var tone = memory.status === "Deleted" ? "bad" : memory.recall === "Cold" ? "neutral" : "good";
      var stateText = memory.status === "Deleted" ? "已遗忘" : memory.recall === "Cold" ? "已归档 · 仍可搜索" : memoryStatusLabel(memory.status);
      return "<button type='button' class='list-row" + (isSelected ? " active" : "") + "' data-action='sec-memory-select' data-readonly='true' data-id='" + e(U, memory.id) + "'>" +
        "<span class='stack'><strong>" + e(U, title) + "</strong><span class='muted'>" + e(U, memory.scopeLabel) + " · v" + e(U, memory.version) + "</span></span>" + badge(U, stateText, tone) + "</button>";
    }).join("") + "</div>";
  }

  function memoryDetail(U, library, memory) {
    if (!memory) return "<div class='card empty'>选择一条记忆查看详情。</div>";
    if (memory.status === "Deleted") {
      var log = library.forgetLog.find(function (item) { return item.id === memory.id; });
      return "<div class='card stack'><div class='between'><h2>已遗忘</h2>" + badge(U, "禁止新注入", "bad") + "</div>" +
        "<p class='muted'>正文已从当前演示状态清空。仅保留最小操作记录，无法展示或恢复已忘记内容。</p>" +
        (log ? "<p>已选清理范围：" + escList(U, log.scope) + "</p><p class='mini muted'>已在途请求：" + e(U, log.inFlight) + "</p>" : "") + "</div>";
    }
    if (library.editingMemoryId === memory.id) {
      var memoryDraft = library.memoryDraft && library.memoryDraft.id === memory.id ? library.memoryDraft : null;
      return "<div class='card stack' data-sec-form='memory'><h2>更正记忆</h2>" +
        note("保存会发布新版本并保留旧版本关系；更正不会自动扩大共享范围。", "warn") +
        field(U, "记忆正文", textArea("memoryText", memoryDraft ? memoryDraft.content : memory.content, 5)) +
        "<div class='row wrap'>" + button(U, "保存为新版本", "sec-memory-save", "primary") + button(U, "取消", "sec-memory-cancel", "quiet") + "</div></div>";
    }
    var archiveLabel = memory.recall === "Cold" ? "恢复为常用" : "归档到冷记忆";
    var previous = memory.history && memory.history.length ? "<div class='divider'></div><h3>版本记录</h3><ul>" + memory.history.map(function (item) {
      return "<li>v" + e(U, item.version) + " · " + e(U, item.label) + "</li>";
    }).join("") + "</ul>" : "";
    return "<div class='card stack'><div class='between wrap'><div><h2>" + e(U, memory.title) + "</h2><p class='muted'>" + e(U, memoryKindLabel(memory.kind)) + " · " + e(U, memory.scopeLabel) + "</p></div>" +
      (memory.recall === "Cold" ? badge(U, "已归档 · 不默认召回", "neutral") : badge(U, memoryStatusLabel(memory.status), "good")) + "</div>" +
      "<p>" + e(U, memory.content) + "</p><div class='detail-grid'>" +
      "<section><h3>来源</h3><p>" + e(U, memory.source) + "</p><details><summary>查看记录标识</summary><p class='mini muted'>" + e(U, memory.sourceRef) + "</p></details></section>" +
      "<section><h3>版本与使用</h3><p>当前 v" + e(U, memory.version) + " · 已使用 " + e(U, memory.uses) + " 次</p><p class='mini muted'>最近更新：" + e(U, memory.updated) + "</p></section></div>" +
      "<div><h3>使用记录</h3><ul>" + memory.usedIn.map(function (item) { return "<li>" + e(U, item) + "</li>"; }).join("") + "</ul>" +
      button(U, "模拟一次引用", "sec-memory-use", "quiet", { "data-id": memory.id }) + "</div>" + previous +
      "<div class='row wrap'>" +
      button(U, "更正", "sec-memory-edit", "", { "data-id": memory.id }) +
      button(U, "共享预览", "sec-memory-share-preview", "", { "data-id": memory.id }) +
      button(U, archiveLabel, "sec-memory-archive", "quiet", { "data-id": memory.id }) +
      button(U, "忘记并清理副本", "sec-memory-forget", "danger", { "data-id": memory.id }) +
      "</div></div>";
  }

  function memoryTab(U, library) {
    var memory = memoryById(library, library.selectedMemoryId);
    var active = library.memories.filter(function (item) { return item.status !== "Deleted"; }).length;
    var candidates = library.candidates.map(function (candidate) {
      return "<article class='list-row'><div class='stack'><strong>" + e(U, candidate.title) + "</strong><span class='muted'>" + e(U, memoryKindLabel(candidate.kind)) + " · " + e(U, candidate.scopeLabel) + " · 来源：" + e(U, candidate.source) + "</span></div>" +
        button(U, "预览来源与范围", "sec-memory-candidate-preview", "quiet", { "data-id": candidate.id }) + "</article>";
    }).join("");
    return "<div class='grid-2'><section class='card stack'><div class='between'><div><h2>记忆</h2><p class='muted'>来源、范围、版本、冷热和使用记录。</p></div><span class='muted'>" + active + " 条可管理</span></div>" +
      memoryList(U, library) + "</section><section>" + memoryDetail(U, library, memory) + "</section></div>" +
      "<section class='card stack'><div class='section-head'><div><h2>待审核记忆候选</h2><p class='muted'>先查看来源与范围，再决定发布或拒绝。</p></div><span class='muted'>" + library.candidates.length + " 条待审核</span></div>" +
      (candidates || "<p class='empty'>当前没有待审核候选。</p>") + "</section>";
  }

  function renderLibrary(U, state) {
    var library = state.library;
    return "<header class='page-head'><div><div class='eyebrow'>工作区资料</div><h1 id='page-title' tabindex='-1'>资料</h1><p class='muted'>查看知识来源、索引状态与可治理的长期记忆。</p></div></header>" +
      note("原型模拟：资料解析、索引、共享和删除只改变本地演示状态，不读写真实知识文件。", "warn") +
      "<nav class='tabs' aria-label='资料分类'>" +
      button(U, "知识库", "sec-library-tab", library.tab === "knowledge" ? "primary" : "quiet", { "data-id": "knowledge", "class": library.tab === "knowledge" ? "active" : "" }) +
      button(U, "记忆", "sec-library-tab", library.tab === "memory" ? "primary" : "quiet", { "data-id": "memory", "class": library.tab === "memory" ? "active" : "" }) + "</nav>" +
      (library.tab === "knowledge" ? knowledgeTab(U, library) : memoryTab(U, library));
  }

  function protocolOptions(current) {
    return selectInput("modelProtocol", [
      ["responses", "OpenAI Responses"],
      ["chat", "OpenAI 兼容 Chat Completions"],
      ["anthropic", "Anthropic Messages"],
      ["gemini", "Gemini 原生协议"]
    ], current);
  }

  function modelPanel(U, settings) {
    var draft = settings.drafts && settings.drafts.models || {};
    var model = Object.assign({}, settings.model, draft.model || {});
    var retrieval = Object.assign({}, settings.retrieval, draft.retrieval || {});
    return "<div class='stack'>" +
      "<section class='card stack'><div class='section-head'><div><h2>默认模型连接</h2><p class='muted'>连接、协议和模型分别保存。连接测试只做原型规则判断。</p></div>" + badge(U, model.result, model.result.indexOf("失败") >= 0 ? "bad" : (model.result === "原型模拟通过" ? "good" : "neutral")) + "</div>" +
      "<div class='form-grid' data-sec-form='model'>" +
      field(U, "协议", protocolOptions(model.protocol)) +
      field(U, "API 地址", "<input class='input' name='modelBaseUrl' value='" + e(U, model.baseUrl) + "' required>") +
      field(U, "模型名称", "<input class='input' name='modelName' value='" + e(U, model.model) + "' required>") +
      field(U, "API 密钥", "<input class='input' type='password' name='modelSecret' value='" + e(U, modelSecretDraft) + "' autocomplete='new-password' placeholder='" + (model.keyConfigured ? "已配置；留空保持不变" : "仅保存在当前页面内存") + "'>") +
      "</div><p class='mini muted'>" + (model.keyConfigured ? "演示状态：已配置密钥，不显示其内容。" : "密钥不会写入共享状态；重载页面后需重新输入。") + "</p>" +
      "<div class='row wrap'>" + button(U, "保存模型配置", "sec-model-save", "primary") + button(U, "测试连接", "sec-model-test", "") + button(U, "清除密钥状态", "sec-model-clear-key", "quiet") + "</div>" +
      (model.testedAt ? "<p class='mini muted'>最近测试：" + e(U, model.testedAt) + " · 未发送真实网络请求</p>" : "") + "</section>" +
      "<section class='card stack'><div class='section-head'><div><h2>知识检索模型</h2><p class='muted'>独立模型配置；更换向量空间后需建立新索引代次。</p></div>" + badge(U, retrieval.result, retrieval.result.indexOf("失败") >= 0 ? "bad" : (retrieval.result === "原型模拟通过" ? "good" : "neutral")) + "</div>" +
      "<div class='form-grid' data-sec-form='retrieval'>" +
      field(U, "检索 API 地址", "<input class='input' name='retrievalBaseUrl' value='" + e(U, retrieval.baseUrl) + "' required>") +
      field(U, "Embedding 模型", "<input class='input' name='retrievalModel' value='" + e(U, retrieval.model) + "' required>") +
      field(U, "向量维度", "<input class='input' type='number' name='retrievalDimensions' min='1' value='" + e(U, retrieval.dimensions) + "' required>") +
      field(U, "检索模型密钥", "<input class='input' type='password' name='retrievalSecret' value='" + e(U, retrievalSecretDraft) + "' autocomplete='new-password' placeholder='" + (retrieval.keyConfigured ? "已配置；留空保持不变" : "仅保存在当前页面内存") + "'>") +
      "</div><p class='mini muted'>当前索引代次：" + e(U, retrieval.generation) + " · 原型测试会显示单条、多条与中文输入样例，但不会调用 API。</p>" +
      "<div class='row wrap'>" + button(U, "保存检索配置", "sec-retrieval-save", "primary") + button(U, "测试检索模型", "sec-retrieval-test", "") + "</div></section></div>";
  }

  function toolPanel(U, settings) {
    var savedScopes = settings.drafts && settings.drafts.tools && settings.drafts.tools.scopes || {};
    return "<section class='card stack'><div class='section-head'><div><h2>工具连接与插件</h2><p class='muted'>安装只加入清单，不会自动授予工具权限。</p></div></div>" +
      settings.tools.map(function (tool) {
        var status = tool.installed ? badge(U, "已安装", "good") : badge(U, "未安装", "neutral");
        var authorization = tool.authorized ? badge(U, "已分配给 " + tool.member, "warn") : badge(U, "未授权", "neutral");
        return "<article class='list-row' data-toolrow='" + e(U, tool.id) + "'><div class='stack'><strong>" + e(U, tool.name) + "</strong><span class='muted'>" + e(U, tool.type) + " · " + status + " " + authorization + "</span></div>" +
          "<div class='stack'>" + field(U, "使用范围", selectInput("toolScope", [
            ["current-task", "仅当前任务"],
            ["current-member", "当前成员"],
            ["current-project", "当前项目"]
          ], savedScopes[tool.id] || tool.scope)) +
          "<div class='row wrap'>" +
          (tool.installed ? "" : button(U, "安装", "sec-tool-install", "primary", { "data-id": tool.id })) +
          (tool.installed ? button(U, tool.authorized ? "撤销授权" : "分配权限", "sec-tool-toggle", tool.authorized ? "quiet" : "", { "data-id": tool.id }) : "") +
          button(U, "保存范围", "sec-tool-scope", "quiet", { "data-id": tool.id }) +
          "</div></div></article>";
      }).join("") +
      note("原型模拟：不安装代码、不连接 MCP；授权范围仅更新本地演示状态。新发现的工具仍需单独检查和授权。", "warn") + "</section>";
  }

  function emailPanel(U, settings) {
    var savedDraft = settings.drafts && settings.drafts.email && settings.drafts.email.email;
    var email = Object.assign({}, settings.email, savedDraft || {});
    var contactDraft = settings.drafts && settings.drafts.email || {};
    var statusTone = email.result.indexOf("失败") >= 0 ? "bad" : email.result === "原型模拟通过" ? "good" : "neutral";
    var contacts = (email.contacts || []).map(function (contact) {
      return "<div class='list-row'><span class='stack'><strong>" + e(U, contact.name) + "</strong><span class='muted'>" + e(U, contact.email) + "</span></span>" +
        (email.to === contact.email ? badge(U, "默认收件人", "good") : button(U, "设为默认收件人", "sec-email-contact-default", "quiet", { "data-id": contact.id })) + "</div>";
    }).join("");
    return "<section class='card stack'><div class='between wrap'><div><h2>邮件连接</h2><p class='muted'>连接测试只检查填写项，不会发送邮件。</p></div>" + badge(U, email.result, statusTone) + "</div>" +
      "<div class='form-grid' data-sec-form='email'>" +
      field(U, "SMTP 主机", "<input class='input' name='smtpHost' value='" + e(U, email.host) + "' required>") +
      field(U, "端口", "<input class='input' type='number' name='smtpPort' min='1' max='65535' value='" + e(U, email.port) + "' required>") +
      field(U, "加密方式", selectInput("smtpEncryption", [["starttls", "STARTTLS"], ["tls", "TLS"], ["none", "不加密（仅测试环境）"]], email.encryption)) +
      field(U, "账号", "<input class='input' name='smtpAccount' value='" + e(U, email.account) + "' required>") +
      field(U, "密码或应用专用密码", "<input class='input' type='password' name='smtpSecret' value='" + e(U, smtpSecretDraft) + "' autocomplete='new-password' placeholder='" + (email.keyConfigured ? "已配置；留空保持不变" : "仅保存在当前页面内存") + "'>") +
      field(U, "发件人名称", "<input class='input' name='smtpSenderName' value='" + e(U, email.senderName) + "' required>") +
      field(U, "发件地址", "<input class='input' type='email' name='smtpFrom' value='" + e(U, email.from) + "' required>") +
      field(U, "默认收件人", "<input class='input' type='email' name='smtpTo' value='" + e(U, email.to) + "' required>") +
      field(U, "Reply-To（选填）", "<input class='input' type='email' name='smtpReplyTo' value='" + e(U, email.replyTo) + "'>") +
      field(U, "汇报频率", selectInput("emailFrequency", [["daily", "每天"], ["weekly", "每周"], ["monthly", "每月"]], email.frequency)) +
      field(U, "静默时段开始", "<input class='input' type='time' name='emailQuietStart' value='" + e(U, email.quietStart) + "' required>") +
      field(U, "静默时段结束", "<input class='input' type='time' name='emailQuietEnd' value='" + e(U, email.quietEnd) + "' required>") +
      "</div><p class='mini muted'>密钥不显示在连接状态或演示记录中。</p><div class='row wrap'>" +
      button(U, "保存邮箱设置", "sec-email-save", "primary") + button(U, "测试连接", "sec-email-test", "") +
      button(U, "发送测试邮件…", "sec-email-send-confirm", "quiet") + "</div>" +
      (email.lastPreview ? "<div class='notice good'>" + e(U, email.lastPreview) + "</div>" : "") +
      "<div class='divider'></div><div class='section-head'><div><h3>通讯录</h3><p class='muted'>联系人和默认收件人仅保存在本地演示状态。</p></div></div>" +
      "<div class='stack'>" + (contacts || "<p class='empty'>尚无联系人。</p>") + "</div>" +
      "<div class='form-grid' data-sec-form='email-contact'>" +
      field(U, "联系人姓名", "<input class='input' name='contactName' value='" + e(U, contactDraft.contactName || "") + "' required>") +
      field(U, "联系人邮箱", "<input class='input' type='email' name='contactEmail' value='" + e(U, contactDraft.contactEmail || "") + "' required>") +
      button(U, "添加联系人", "sec-email-contact-add", "quiet") + "</div>" +
      note("原型模拟：连接测试不访问 SMTP；测试邮件需单独确认，但确认后也不会真实发出。", "warn") + "</section>";
  }

  function budgetPanel(U, settings) {
    var budget = Object.assign({}, settings.budget, settings.drafts && settings.drafts.budget && settings.drafts.budget.budget || {});
    return "<section class='card stack'><div class='section-head'><div><h2>运行预算</h2><p class='muted'>上限只影响原型演示状态；生产账本仍需以每次调用的已确认、估算和待结算用量为准。</p></div></div>" +
      "<div class='form-grid' data-sec-form='budget'>" +
      field(U, "每月费用上限（人民币，¥）", "<input class='input' type='number' name='budgetMonthly' min='0' step='1' value='" + e(U, budget.monthly) + "' required>") +
      field(U, "每月 Token 上限", "<input class='input' type='number' name='budgetTokens' min='0' value='" + e(U, budget.tokens) + "' required>") +
      field(U, "每月模型调用上限", "<input class='input' type='number' name='budgetCalls' min='0' value='" + e(U, budget.calls) + "' required>") +
      field(U, "同时运行任务上限", "<input class='input' type='number' name='budgetConcurrency' min='1' max='3' value='" + e(U, budget.concurrency) + "' required>") +
      field(U, "单个任务最长运行（分钟）", "<input class='input' type='number' name='budgetDuration' min='1' value='" + e(U, budget.duration) + "' required>") +
      "</div><div class='row wrap'>" + button(U, "保存预算", "sec-budget-save", "primary") + "</div></section>";
  }

  function automationPanel(U, settings) {
    var automation = Object.assign({}, settings.automation, settings.drafts && settings.drafts.automation && settings.drafts.automation.automation || {});
    return "<section class='card stack'><div class='section-head'><div><h2>自动化</h2><p class='muted'>时区和停机期间的漏跑策略随规则一起保存。</p></div>" + badge(U, automation.enabled ? "已启用" : "已暂停", automation.enabled ? "good" : "neutral") + "</div>" +
      "<div class='form-grid' data-sec-form='automation'>" +
      "<label class='switch-row'><input id='automationEnabled' type='checkbox' name='automationEnabled'" + checked(automation.enabled) + ">启用每日报告生成（原型任务）</label>" +
      field(U, "执行时间", "<input class='input' type='time' name='automationSchedule' value='" + e(U, automation.schedule) + "' required>") +
      field(U, "时区", selectInput("automationTimezone", [["Asia/Shanghai", "中国标准时间 · Asia/Shanghai"], ["UTC", "协调世界时 · UTC"], ["America/Los_Angeles", "太平洋时间 · America/Los_Angeles"]], automation.timezone)) +
      field(U, "停机期间漏跑", selectInput("automationMissed", [["skip", "跳过"], ["merge", "合并到下一次"], ["catch-up", "有限补跑"]], automation.missed)) +
      field(U, "有限补跑上限", "<input class='input' type='number' name='automationCatchUp' min='0' max='3' value='" + e(U, automation.catchUp) + "' required>") +
      "</div><div class='row wrap'>" + button(U, "保存自动化", "sec-automation-save", "primary") + button(U, "模拟处理一次漏跑", "sec-automation-run", "quiet") + "</div>" +
      "<p class='mini muted'>下次计划：" + e(U, automation.next) + "</p><p class='mini muted'>" + e(U, automation.lastRun) + "</p>" +
      note("原型模拟：不会创建定时任务。实际发生实例应按规则版本和计划时间去重，并使用有限补跑策略。", "warn") + "</section>";
  }

  function backupPanel(U, settings) {
    var backup = settings.backup;
    var draft = settings.drafts && settings.drafts.backup || {};
    var backupSchedule = draft.schedule || backup.schedule;
    var backupRetention = draft.retention || backup.retention;
    return "<section class='card stack'><div class='section-head'><div><h2>工作区备份与恢复</h2><p class='muted'>恢复目标使用新目录，避免覆盖正在使用的工作区。</p></div></div>" +
      field(U, "当前工作区目录", "<input class='input' id='workspace-directory' name='workspace-directory' value='" + e(U, backup.workspace) + "' readonly>") +
      "<div class='form-grid' data-sec-form='backup'>" +
      field(U, "备份周期", selectInput("backupSchedule", [["hourly", "每小时"], ["daily", "每天"], ["weekly", "每周"]], backupSchedule)) +
      field(U, "保留备份数", "<input class='input' type='number' name='backupRetention' min='1' max='365' value='" + e(U, backupRetention) + "' required>") +
      button(U, "保存备份设置", "sec-backup-settings-save", "primary") + "</div>" +
      "<div class='row wrap'>" + button(U, "立即模拟备份", "sec-backup-create", "primary") + button(U, "预览诊断导出字段", "sec-backup-diagnostics-preview", "quiet") + "</div>" +
      "<div class='stack'>" + backup.backups.map(function (item) {
        var tone = item.keyAvailable ? "good" : "bad";
        return "<div class='list-row'><span class='stack'><strong>" + e(U, item.name) + "</strong><span class='muted'>" + e(U, item.path) + "</span></span>" +
          badge(U, item.status, tone) + button(U, "恢复到新目录…", "sec-backup-restore-open", "", { "data-id": item.id }) + "</div>";
      }).join("") + "</div>" +
      (backup.lastRestore ? "<div class='notice warn'>" + e(U, backup.lastRestore) + "</div>" : "") +
      note("原型模拟：不复制工作区文件。演示清单包含一个缺少恢复密钥的旧备份；实际恢复前还应检查最新记忆撤回水位。", "warn") + "</section>";
  }

  function accessPanel(U, settings, S) {
    var access = settings.access;
    var draft = settings.drafts && settings.drafts.access || {};
    var apiScope = draft.apiScope || "current-task";
    var webhookEnabled = draft.webhookEnabled == null ? access.webhook.enabled : draft.webhookEnabled;
    var webhookScope = draft.webhookScope || access.webhook.scope;
    var recoveryName = draft.recoverySessionName || "恢复后的浏览器";
    var sessionRows = access.sessions.map(function (session) {
      return "<div class='list-row'><span class='stack'><strong>" + e(U, session.name) + (session.current ? " · 当前设备" : "") + "</strong><span class='muted'>" + e(U, session.device) + " · 最近活动 " + e(U, session.lastSeen) + "</span></span>" +
        badge(U, session.status, session.status === "有效" ? "good" : "neutral") +
        (session.status === "有效" ? button(U, "撤销会话", "sec-access-revoke-open", "danger", { "data-id": session.id }) : "") + "</div>";
    }).join("");
    var credentialRows = (access.credentials || []).map(function (credential) {
      return "<div class='list-row'><span class='stack'><strong>" + e(U, credential.label || "受限 API 凭据") + "</strong><span class='muted'>" + e(U, credential.scopeLabel) + " · " + e(U, credential.status) + " · " + e(U, credential.createdAt) + "</span></span>" +
        (credential.status === "有效（模拟）" ? button(U, "撤销记录", "sec-access-credential-revoke", "danger", { "data-id": credential.id }) : "") + "</div>";
    }).join("");
    return "<section class='card stack'><div class='section-head'><div><h2>账号与访问</h2><p class='muted'>本地配对码、当前浏览器会话和撤销记录。</p></div>" + badge(U, access.loggedIn ? "已登录" : "未登录", access.loggedIn ? "good" : "warn") + "</div>" +
      "<p>浏览器配对状态：" + badge(U, access.paired ? "已配对" : "未配对", access.paired ? "good" : "neutral") + "</p>" +
      "<div class='row wrap'>" + button(U, "生成一次性配对码", "sec-access-pair", "") +
      (access.loggedIn ? button(U, "退出登录", "sec-access-logout", "quiet") : "") + "</div>" +
      (access.pairingCode ? "<div class='notice good'><strong>演示配对码：" + e(U, access.pairingCode) + "</strong><br><span class='mini'>有效期至 " + e(U, access.pairingExpiry) + "。请在下方输入后模拟登录。</span></div>" : "") +
      "<div class='form-grid' data-sec-form='access'>" + field(U, "一次性配对码", "<input class='input' name='pairingCodeInput' value='' autocomplete='one-time-code'>") +
      button(U, "使用配对码登录", "sec-access-login", "primary") + "</div>" +
      "<div class='divider'></div><div class='section-head'><div><h3>受限 API 凭据</h3><p class='muted'>只记录权限范围，不生成或保存真实令牌。</p></div></div>" +
      "<div class='notice'>当前任务：" + e(U, permissionTarget(S, "current-task")) + "<br>当前成员：" + e(U, permissionTarget(S, "current-member")) + "<br>当前项目：" + e(U, permissionTarget(S, "current-project")) + "</div>" +
      "<div class='form-grid' data-sec-form='access'>" + field(U, "凭据范围", selectInput("apiScope", [["current-task", "仅当前任务"], ["current-member", "当前成员"], ["current-project", "当前项目"]], apiScope)) +
      button(U, "生成模拟凭据记录", "sec-access-credential-create", "quiet") + "</div><div class='stack'>" + (credentialRows || "<p class='empty'>尚无凭据记录。</p>") + "</div>" +
      "<div class='divider'></div><div class='section-head'><div><h3>Webhook 范围</h3><p class='muted'>保存时只更新本地模拟状态，不创建回调地址。</p></div></div>" +
      "<div class='form-grid' data-sec-form='access'><label class='switch-row'><input id='webhookEnabled' type='checkbox' name='webhookEnabled'" + checked(webhookEnabled) + ">启用 Webhook（模拟）</label>" +
      field(U, "Webhook 权限范围", selectInput("webhookScope", [["current-task", "仅当前任务"], ["current-member", "当前成员"], ["current-project", "当前项目"]], webhookScope)) +
      button(U, "保存 Webhook 范围", "sec-access-webhook-save", "quiet") + "</div><p class='mini muted'>当前状态：" + e(U, access.webhook.status) + "</p>" +
      "<div class='divider'></div><div class='section-head'><div><h3>恢复入口</h3><p class='muted'>使用部署端恢复码模拟建立新会话；此处不修改密码。</p></div></div>" +
      "<div class='form-grid' data-sec-form='access'>" + field(U, "部署端恢复码", "<input class='input' type='password' name='recoveryCode' autocomplete='off' placeholder='原型示例：RECOVERY-DEMO'>") +
      field(U, "新会话名称", "<input class='input' name='recoverySessionName' value='" + e(U, recoveryName) + "' required>") +
      button(U, "模拟恢复并建立新会话", "sec-access-recovery", "quiet") + "</div><p class='mini muted'>演示恢复码只在提交时校验，不保存输入内容；不会修改账号密码。</p>" +
      "<div class='section-head'><h3>有效会话</h3><span class='muted'>撤销会阻止该会话后续访问。</span></div><div class='stack'>" + sessionRows + "</div>" +
      note("原型模拟：不创建令牌、不建立身份认证连接。生产界面不得在 URL、日志或长期浏览器存储中保存访问令牌。", "warn") + "</section>";
  }

  function settingsBody(U, settings, S) {
    switch (settings.tab) {
      case "models": return modelPanel(U, settings);
      case "tools": return toolPanel(U, settings);
      case "email": return emailPanel(U, settings);
      case "budget": return budgetPanel(U, settings);
      case "automation": return automationPanel(U, settings);
      case "backup": return backupPanel(U, settings);
      case "access": return accessPanel(U, settings, S);
      default: settings.tab = "models"; return modelPanel(U, settings);
    }
  }

  function renderSettings(U, state, S) {
    var settings = state.settings;
    return "<header class='page-head'><div><div class='eyebrow'>工作区配置</div><h1 id='page-title' tabindex='-1'>设置</h1><p class='muted'>按需要配置连接、能力、安全边界和恢复方式。</p></div></header>" +
      note("原型模拟：所有设置只更新本地演示状态，不连接真实模型、邮箱或外部服务。", "warn") +
      "<div class='settings-layout'><nav class='settings-nav' aria-label='设置分类'>" + settingTabs.map(function (tab) {
        return button(U, tab[1], "sec-settings-tab", settings.tab === tab[0] ? "primary" : "quiet", { "data-id": tab[0], "class": settings.tab === tab[0] ? "active" : "" });
      }).join("") + "</nav><div class='stack'>" + settingsBody(U, settings, S) + "</div></div>";
  }

  function render(page, S, U) {
    var state = init(S);
    if (page === "team") return renderTeam(U, state, S);
    if (page === "library") return renderLibrary(U, state);
    if (page === "settings") return renderSettings(U, state, S);
    return "<header class='page-head'><div><div class='eyebrow'>工作区</div><h1 id='page-title' tabindex='-1'>页面不存在</h1></div></header>";
  }

  function attr(buttonElement, name) {
    return buttonElement && buttonElement.dataset ? buttonElement.dataset[name] || "" : "";
  }

  function modelDraft(buttonElement, model) {
    return {
      protocol: valueFrom(buttonElement, "modelProtocol", model.protocol),
      baseUrl: valueFrom(buttonElement, "modelBaseUrl", model.baseUrl),
      model: valueFrom(buttonElement, "modelName", model.model)
    };
  }

  function retrievalDraft(buttonElement, retrieval) {
    return {
      baseUrl: valueFrom(buttonElement, "retrievalBaseUrl", retrieval.baseUrl),
      model: valueFrom(buttonElement, "retrievalModel", retrieval.model),
      dimensions: valueFrom(buttonElement, "retrievalDimensions", retrieval.dimensions)
    };
  }

  function emailDraft(buttonElement, email) {
    return {
      host: valueFrom(buttonElement, "smtpHost", email.host),
      port: valueFrom(buttonElement, "smtpPort", email.port),
      encryption: valueFrom(buttonElement, "smtpEncryption", email.encryption),
      account: valueFrom(buttonElement, "smtpAccount", email.account),
      senderName: valueFrom(buttonElement, "smtpSenderName", email.senderName),
      from: valueFrom(buttonElement, "smtpFrom", email.from),
      to: valueFrom(buttonElement, "smtpTo", email.to),
      replyTo: valueFrom(buttonElement, "smtpReplyTo", email.replyTo),
      frequency: valueFrom(buttonElement, "emailFrequency", email.frequency),
      quietStart: valueFrom(buttonElement, "emailQuietStart", email.quietStart),
      quietEnd: valueFrom(buttonElement, "emailQuietEnd", email.quietEnd)
    };
  }

  function draftFor(settings, tab) {
    if (!settings.drafts) settings.drafts = {};
    if (!settings.drafts[tab]) settings.drafts[tab] = {};
    return settings.drafts[tab];
  }

  function captureSettingsDraft(settings, buttonElement) {
    var tab = settings.tab;
    var draft = draftFor(settings, tab);
    if (tab === "models") {
      draft.model = modelDraft(buttonElement, settings.model);
      draft.retrieval = retrievalDraft(buttonElement, settings.retrieval);
      if (typeof document !== "undefined") {
        var modelSecret = document.querySelector("[name='modelSecret']");
        var retrievalSecret = document.querySelector("[name='retrievalSecret']");
        if (modelSecret) modelSecretDraft = modelSecret.value;
        if (retrievalSecret) retrievalSecretDraft = retrievalSecret.value;
      }
      return true;
    }
    if (tab === "tools") {
      draft.scopes = {};
      if (typeof document !== "undefined") {
        Array.prototype.slice.call(document.querySelectorAll("[data-toolrow]")).forEach(function (row) {
          var select = row.querySelector("[name='toolScope']");
          if (select) draft.scopes[row.getAttribute("data-toolrow")] = select.value;
        });
      }
      return true;
    }
    if (tab === "email") {
      draft.email = emailDraft(buttonElement, settings.email);
      draft.contactName = valueFrom(buttonElement, "contactName", draft.contactName || "");
      draft.contactEmail = valueFrom(buttonElement, "contactEmail", draft.contactEmail || "");
      if (typeof document !== "undefined") {
        var smtpSecret = document.querySelector("[name='smtpSecret']");
        if (smtpSecret) smtpSecretDraft = smtpSecret.value;
      }
      return true;
    }
    if (tab === "budget") {
      draft.budget = {
        monthly: valueFrom(buttonElement, "budgetMonthly", settings.budget.monthly),
        tokens: valueFrom(buttonElement, "budgetTokens", settings.budget.tokens),
        calls: valueFrom(buttonElement, "budgetCalls", settings.budget.calls),
        concurrency: valueFrom(buttonElement, "budgetConcurrency", settings.budget.concurrency),
        duration: valueFrom(buttonElement, "budgetDuration", settings.budget.duration)
      };
      return true;
    }
    if (tab === "automation") {
      draft.automation = {
        enabled: inputChecked(buttonElement, "[name='automationEnabled']"),
        schedule: valueFrom(buttonElement, "automationSchedule", settings.automation.schedule),
        timezone: valueFrom(buttonElement, "automationTimezone", settings.automation.timezone),
        missed: valueFrom(buttonElement, "automationMissed", settings.automation.missed),
        catchUp: valueFrom(buttonElement, "automationCatchUp", settings.automation.catchUp)
      };
      return true;
    }
    if (tab === "backup") {
      draft.schedule = valueFrom(buttonElement, "backupSchedule", settings.backup.schedule);
      draft.retention = valueFrom(buttonElement, "backupRetention", settings.backup.retention);
      return true;
    }
    if (tab === "access") {
      draft.apiScope = valueFrom(buttonElement, "apiScope", draft.apiScope || "current-task");
      draft.webhookEnabled = inputChecked(buttonElement, "[name='webhookEnabled']");
      draft.webhookScope = valueFrom(buttonElement, "webhookScope", settings.access.webhook.scope);
      draft.recoverySessionName = valueFrom(buttonElement, "recoverySessionName", draft.recoverySessionName || "恢复后的浏览器");
      return true;
    }
    return false;
  }

  function clearSettingsDraft(settings, tab) {
    if (settings.drafts) delete settings.drafts[tab];
    if (tab === "models") {
      modelSecretDraft = "";
      retrievalSecretDraft = "";
    } else if (tab === "email") {
      smtpSecretDraft = "";
    }
  }

  function clearSettingsPartDraft(settings, tab, part) {
    if (!settings.drafts || !settings.drafts[tab]) return;
    delete settings.drafts[tab][part];
    if (!Object.keys(settings.drafts[tab]).length) delete settings.drafts[tab];
  }

  function idWithSerial(prefix) {
    serial += 1;
    return prefix + "-" + Date.now().toString(36) + "-" + serial.toString(36);
  }

  function openArchiveModal(U, team, member) {
    team.pendingArchiveId = member.id;
    var candidates = team.members.filter(function (item) { return item.id !== member.id && item.status === "active"; });
    if (!member.activeTasks.length) {
      confirm(U, "归档成员", "该成员没有活跃任务。确认后将停止分配新任务，历史记录仍可查看。", "归档成员", "sec-team-archive-confirm", "danger");
      return;
    }
    if (!candidates.length) {
      toast(U, "当前没有可接手任务的成员，请先新增或恢复一位成员。");
      return;
    }
    var handoverDraft = team.handoverDraft && team.handoverDraft.id === member.id ? team.handoverDraft : null;
    var options = candidates.map(function (item) {
      return "<option value='" + escapeHtml(item.id) + "'" + selected(handoverDraft && handoverDraft.toId, item.id) + ">" + escapeHtml(item.name + " · " + item.role) + "</option>";
    }).join("");
    var body = "<p>归档前先交接 " + escapeHtml(member.activeTasks.length) + " 项活跃任务。确认后才会切换责任人。</p>" +
      "<ul>" + member.activeTasks.map(function (task) { return "<li>" + escapeHtml(task) + "</li>"; }).join("") + "</ul>" +
      field(U, "交接给", "<select class='input' name='handoverTo'>" + options + "</select>") +
      field(U, "交接说明（选填）", "<textarea class='input' name='handoverNote' rows='2'>" + escapeHtml(handoverDraft ? handoverDraft.note : "已保存当前进度与下一步。") + "</textarea>");
    var footer = button(U, "取消", "sec-close-modal", "quiet") +
      button(U, "完成交接并归档", "sec-team-archive-handover", "danger");
    modal(U, "交接活跃任务", body, footer);
  }

  function openMemoryShare(U, memory, memberCount) {
    var body = "<div class='stack'><p>以下内容将从 <strong>" + escapeHtml(memory.scopeLabel) + "</strong> 发布到 <strong>整个工作区</strong>。</p>" +
      "<blockquote>" + escapeHtml(memory.content || "") + "</blockquote>" +
      "<div class='notice warn'>影响：工作区内的 " + escapeHtml(memberCount) + " 位在岗成员可在后续授权任务中检索这条新版本记忆。现有任务上下文不会被追溯改写。</div>" +
      "<p class='mini muted'>来源：" + escapeHtml(memory.source) + " · 当前版本 v" + escapeHtml(memory.version) + "</p></div>";
    modal(U, "共享预览", body, button(U, "取消", "sec-close-modal", "quiet") +
      button(U, "确认发布到工作区", "sec-memory-share-confirm", "primary"));
  }

  function openForget(U, library, memory) {
    library.pendingForgetId = memory.id;
    var defaultScopes = ["正文与原始候选", "检索索引与缓存", "摘要与派生候选", "续接检查点", "可控导出副本"];
    var chosen = library.forgetDraft || defaultScopes;
    function scopeChecked(value) { return chosen.indexOf(value) >= 0 ? " checked" : ""; }
    var body = "<p>请选择要清理的副本范围。删除会立即停止未来注入；无法承诺撤回模型在途请求已经收到的文本。</p>" +
      "<div class='stack'>" +
      "<label class='switch-row'><input type='checkbox' data-forget-scope value='正文与原始候选'" + scopeChecked("正文与原始候选") + ">正文与原始候选</label>" +
      "<label class='switch-row'><input type='checkbox' data-forget-scope value='检索索引与缓存'" + scopeChecked("检索索引与缓存") + ">检索索引与缓存</label>" +
      "<label class='switch-row'><input type='checkbox' data-forget-scope value='摘要与派生候选'" + scopeChecked("摘要与派生候选") + ">摘要与派生候选</label>" +
      "<label class='switch-row'><input type='checkbox' data-forget-scope value='续接检查点'" + scopeChecked("续接检查点") + ">续接检查点</label>" +
      "<label class='switch-row'><input type='checkbox' data-forget-scope value='可控导出副本'" + scopeChecked("可控导出副本") + ">可控导出副本</label></div>" +
      "<div class='notice bad'>在途任务 TASK-236 可能已经收到这段文本。系统不能撤回已发送内容；如清理检查点导致无法续跑，应从安全边界新建 Run。已发送给他人的文件不在本次可撤回范围内。</div>";
    modal(U, "忘记并清理记忆", body, button(U, "取消", "sec-close-modal", "quiet") +
      button(U, "确认忘记所选范围", "sec-memory-forget-confirm", "danger"));
  }

  function openCitation(U, doc) {
    var body = doc.citation ?
      "<div class='stack'><p><strong>" + escapeHtml(doc.name) + " · v" + escapeHtml(doc.version) + "</strong></p>" +
      "<p class='muted'>第 " + escapeHtml(doc.citation.page) + " 页 · 段落 " + escapeHtml(doc.citation.paragraph) + "</p>" +
      "<blockquote>" + escapeHtml(doc.citation.text) + "</blockquote>" +
      "<p class='mini muted'>本预览来自原型固定样例，用于展示引用应落到文件版本和页段位置。</p></div>" :
      "<div class='stack'><p>" + escapeHtml(doc.name) + " · v" + escapeHtml(doc.version) + "</p>" +
      note("当前版本没有可打开的引用位置，因为解析失败且没有生成可用片段。", "bad") +
      "<p>" + escapeHtml(doc.error || "暂无引用。") + "</p></div>";
    modal(U, "引用预览", body, button(U, "关闭", "sec-close-modal", "quiet"));
  }

  function openIndexModal(U, library) {
    var index = library.index;
    var building = index.rebuildStatus === "building" && index.candidateGeneration;
    var body = building ?
      "<div class='stack'><p>当前正在构建候选索引代次 <strong>" + escapeHtml(index.candidateGeneration) + "</strong>。</p>" +
      "<p>当前可用代次：<strong>" + escapeHtml(index.currentGeneration) + "</strong></p>" +
      "<div class='notice good'>构建期间继续使用旧代次；模拟完成后才会切换当前版本。</div></div>" :
      "<div class='stack'><p>当前可用代次：<strong>" + escapeHtml(index.currentGeneration) + "</strong></p>" +
      "<p>候选代次：尚未启动。</p>" +
      field(U, "候选代次名称", "<input class='input' name='indexGeneration' value='" + escapeHtml(index.candidateDraftSet ? index.candidateDraft : (index.currentGeneration + "-next")) + "' required>") +
      "<p class='mini muted'>原型仅记录代次状态，不扫描文件或调用检索服务。</p></div>";
    var footer = button(U, "关闭", "sec-close-modal", "quiet") +
      (building ? button(U, "模拟构建完成并切换", "sec-index-rebuild-complete", "primary") : button(U, "启动重建", "sec-index-rebuild-start", "primary"));
    modal(U, "知识索引代次", body, footer);
  }

  function openMemoryCandidate(U, library, candidate) {
    library.pendingCandidateId = candidate.id;
    var body = "<div class='stack'><p><strong>" + escapeHtml(candidate.title) + "</strong></p>" +
      "<p>类型：" + escapeHtml(memoryKindLabel(candidate.kind)) + " · 范围：" + escapeHtml(candidate.scopeLabel) + "</p>" +
      "<p>来源：" + escapeHtml(candidate.source) + "</p><details><summary>查看记录标识</summary><p class='mini muted'>" + escapeHtml(candidate.sourceRef || "无记录标识") + "</p></details>" +
      "<blockquote>" + escapeHtml(candidate.content) + "</blockquote>" +
      "<div class='notice warn'>发布后会新增一条可治理记忆；不会直接改变现有任务上下文。</div></div>";
    modal(U, "审核记忆候选", body, button(U, "取消", "sec-close-modal", "quiet") +
      button(U, "拒绝候选", "sec-memory-candidate-reject", "danger") +
      button(U, "发布为记忆", "sec-memory-candidate-publish", "primary"));
  }

  function openBackupDiagnostics(U, backup) {
    var body = "<div class='stack'><p>诊断预览包含：</p><ul><li>客户端版本、操作系统标识与时间戳</li><li>备份计划、保留数量与最近备份状态</li><li>脱敏后的恢复错误类别和撤回水位状态</li></ul>" +
      "<p>不会包含 API 密钥、密码、配对码、文件正文、记忆正文或恢复令牌。</p>" +
      "<p class='mini muted'>当前原型备份计划：" + escapeHtml({ hourly: "每小时", daily: "每天", weekly: "每周" }[backup.schedule] || backup.schedule) + " · 保留 " + escapeHtml(backup.retention) + " 份。</p>" +
      note("这里只展示字段范围，不创建或下载诊断文件。", "warn") + "</div>";
    modal(U, "诊断导出预览", body, button(U, "关闭", "sec-close-modal", "quiet"));
  }

  function openBackupRestore(U, backup, item) {
    var body = "<p>恢复会写入新目录，不覆盖当前工作区。</p>" +
      field(U, "新恢复目录", "<input class='input' name='restoreDirectory' value='" + escapeHtml(backup.restoreDirectoryDraftSet ? backup.restoreDirectoryDraft : (backup.workspace + "-restore")) + "' required>") +
      "<p class='mini muted'>备份：" + escapeHtml(item.name) + " · " + escapeHtml(item.path) + "</p>" +
      (item.keyAvailable ? note("原型将记录恢复目标，不会复制真实文件。", "warn") : note("该备份缺少恢复密钥。提交后会保留目录输入并显示阻塞状态，不会开始恢复。", "bad"));
    modal(U, "恢复到新目录", body, button(U, "取消", "sec-close-modal", "quiet") +
      button(U, "检查并恢复", "sec-backup-restore", "primary", { "data-id": item.id }));
  }

  function openRevoke(U, access, session) {
    access.pendingRevokeId = session.id;
    confirm(U, "撤销会话", "将撤销“" + e(U, session.name) + "”（" + e(U, session.device) + "）的后续访问。此操作不会影响其他会话。", "撤销会话", "sec-access-revoke-confirm", "danger");
  }

  function captureModalDraft(buttonElement, state) {
    if (typeof document === "undefined") return false;
    var dialog = buttonElement && buttonElement.closest ? buttonElement.closest("dialog") : null;
    if (!dialog) dialog = document.querySelector("dialog[open]");
    if (!dialog) return false;
    var changed = false;
    var restoreDirectory = dialog.querySelector("[name='restoreDirectory']");
    if (restoreDirectory) {
      state.settings.backup.restoreDirectoryDraft = restoreDirectory.value;
      state.settings.backup.restoreDirectoryDraftSet = true;
      changed = true;
    }
    var indexGeneration = dialog.querySelector("[name='indexGeneration']");
    if (indexGeneration) {
      state.library.index.candidateDraft = indexGeneration.value;
      state.library.index.candidateDraftSet = true;
      changed = true;
    }
    var forgetInputs = dialog.querySelectorAll("[data-forget-scope]");
    if (forgetInputs.length) {
      state.library.forgetDraft = Array.prototype.slice.call(forgetInputs).filter(function (input) { return input.checked; }).map(function (input) { return input.value; });
      changed = true;
    }
    var handoverTo = dialog.querySelector("[name='handoverTo']");
    if (handoverTo && state.team.pendingArchiveId) {
      state.team.handoverDraft = {
        id: state.team.pendingArchiveId,
        toId: handoverTo.value,
        note: (dialog.querySelector("[name='handoverNote']") || {}).value || ""
      };
      changed = true;
    }
    return changed;
  }

  function captureDrafts(S) {
    if (!S || typeof document === "undefined") return false;
    var state = init(S);
    var changed = false;
    var teamForm = document.querySelector("[data-sec-form='team']");
    if (teamForm && state.team.mode === "edit") {
      captureTeamDraft(state.team, teamForm);
      changed = true;
    }
    var memoryForm = document.querySelector("[data-sec-form='memory']");
    if (memoryForm && state.library.editingMemoryId) {
      captureMemoryDraft(state.library, memoryForm);
      changed = true;
    }
    var knowledgeForm = document.querySelector("[data-sec-form='knowledge']");
    if (knowledgeForm) {
      var selectedName = fileName(knowledgeForm, "knowledgeFile");
      if (selectedName) {
        state.library.selectedFileName = selectedName;
        changed = true;
      }
    }
    if (document.querySelector(".settings-layout")) {
      var activeTab = document.querySelector(".settings-nav [data-action='sec-settings-tab'].active");
      var visibleTab = activeTab && activeTab.dataset ? activeTab.dataset.id : "";
      if (!visibleTab || visibleTab === state.settings.tab) {
        var settingsForm = document.querySelector("[data-sec-form]");
        changed = captureSettingsDraft(state.settings, settingsForm) || changed;
      }
    }
    changed = captureModalDraft(null, state) || changed;
    return changed;
  }

  function handle(action, buttonElement, S, U) {
    var state = init(S);
    var team = state.team;
    var library = state.library;
    var settings = state.settings;
    var id = attr(buttonElement, "id");
    var member;
    var memory;
    var doc;
    var item;

    switch (action) {
      case "sec-team-select":
        captureTeamDraft(team, buttonElement);
        team.selectedId = id;
        team.mode = "detail";
        commit(S, U);
        return true;
      case "sec-team-new":
        captureTeamDraft(team, buttonElement);
        team.mode = "edit";
        team.editingId = null;
        commit(S, U);
        return true;
      case "sec-team-edit":
        team.selectedId = id || team.selectedId;
        team.mode = "edit";
        team.editingId = team.selectedId;
        commit(S, U);
        return true;
      case "sec-team-cancel":
        captureTeamDraft(team, buttonElement);
        team.mode = "detail";
        team.editingId = null;
        commit(S, U);
        return true;
      case "sec-team-save":
        var memberName = valueFrom(buttonElement, "memberName", "").trim();
        var memberRole = valueFrom(buttonElement, "memberRole", "").trim();
        if (!memberName || !memberRole) {
          captureTeamDraft(team, buttonElement);
          commit(S, U, "", false);
          toast(U, "请填写成员姓名和岗位。");
          return true;
        }
        member = team.editingId ? memberById(team, team.editingId) : null;
        if (member) {
          var oldMemberName = member.name;
          member.name = memberName;
          member.role = memberRole;
          member.responsibilities = listFrom(inputValue(buttonElement, "memberResponsibilities"));
          member.capabilities = listFrom(inputValue(buttonElement, "memberCapabilities"));
          if (S.tasks && typeof S.tasks === "object" && oldMemberName !== memberName) {
            Object.keys(S.tasks).forEach(function (taskKey) {
              var task = S.tasks[taskKey];
              if (task && task.owner === oldMemberName) task.owner = memberName;
            });
          }
        } else {
          member = {
            id: idWithSerial("member"),
            name: memberName,
            role: memberRole,
            status: "active",
            accepting: true,
            activeTasks: [],
            responsibilities: listFrom(inputValue(buttonElement, "memberResponsibilities")),
            capabilities: listFrom(inputValue(buttonElement, "memberCapabilities")),
            recent: "刚刚"
          };
          team.members.push(member);
          team.selectedId = member.id;
        }
        team.editingId = null;
        team.mode = "detail";
        team.draft = null;
        commit(S, U, "成员信息已保存。");
        return true;
      case "sec-team-pause":
        member = memberById(team, id || team.selectedId);
        if (!member || member.status === "archived") {
          toast(U, "已归档成员不能恢复接单，请先取消归档。");
          return true;
        }
        member.accepting = !member.accepting;
        commit(S, U, member.accepting ? "已恢复接单。" : "已暂停接单；现有任务继续保留。");
        return true;
      case "sec-team-archive":
        syncTeamTasks(team, S);
        member = memberById(team, id || team.selectedId);
        if (!member || member.status === "archived") return true;
        openArchiveModal(U, team, member);
        commit(S, U, "", false);
        return true;
      case "sec-team-archive-confirm":
        member = memberById(team, team.pendingArchiveId || team.selectedId);
        if (member) {
          member.status = "archived";
          member.accepting = false;
          team.mode = "detail";
          team.pendingArchiveId = null;
          commit(S, U, "成员已归档。");
        }
        closeModal(U);
        return true;
      case "sec-team-archive-handover":
        syncTeamTasks(team, S);
        member = memberById(team, team.pendingArchiveId);
        if (!member) {
          toast(U, "成员状态已变化，请重新打开交接。");
          return true;
        }
        var targetId = inputValue(buttonElement, "handoverTo");
        var target = memberById(team, targetId);
        if (!target || target.status !== "active" || target.id === member.id) {
          captureModalDraft(buttonElement, state);
          commit(S, U, "", false);
          toast(U, "请选择一位仍在岗的接手成员。");
          return true;
        }
        var transferred = member.activeTasks.slice();
        target.activeTasks = target.activeTasks.concat(transferred);
        if (S.tasks && typeof S.tasks === "object") {
          Object.keys(S.tasks).forEach(function (taskKey) {
            var task = S.tasks[taskKey];
            if (task && task.owner === member.name && task.phase !== "done" && task.phase !== "stopped" &&
              task.phase !== "draft" && task.phase !== "queued") task.owner = target.name;
          });
        }
        team.handovers.push({
          fromId: member.id,
          fromName: member.name,
          toId: target.id,
          toName: target.name,
          tasks: transferred,
          note: inputValue(buttonElement, "handoverNote"),
          at: "刚刚"
        });
        member.activeTasks = [];
        member.status = "archived";
        member.accepting = false;
        team.selectedId = member.id;
        team.mode = "detail";
        team.pendingArchiveId = null;
        closeModal(U);
        commit(S, U, "活跃任务已交接，成员已归档。");
        return true;
      case "sec-close-modal":
        var modalChanged = captureModalDraft(buttonElement, state);
        closeModal(U);
        if (modalChanged) commit(S, U);
        return true;
      case "sec-library-tab":
        captureMemoryDraft(library, buttonElement);
        var knowledgeFileName = fileName(buttonElement, "knowledgeFile");
        if (knowledgeFileName) library.selectedFileName = knowledgeFileName;
        library.tab = id === "memory" ? "memory" : "knowledge";
        commit(S, U);
        return true;
      case "sec-knowledge-import":
        var incomingName = fileName(buttonElement, "knowledgeFile") || library.selectedFileName || "演示导入资料.pdf";
        library.selectedFileName = incomingName;
        doc = {
          id: idWithSerial("doc"),
          name: incomingName,
          scope: "当前工作区",
          version: 1,
          status: "failed",
          error: "原型解析器演示：文件内容未上传或读取。请选择真实服务后再导入。",
          pages: 0,
          chunks: 0,
          citation: null,
          updated: "刚刚"
        };
        library.documents.unshift(doc);
        library.selectedDocId = doc.id;
        commit(S, U, "已登记演示资料，并记录一次解析失败。");
        return true;
      case "sec-knowledge-retry":
        doc = documentById(library, id);
        if (doc) {
          doc.status = "processing";
          doc.error = "";
          doc.status = "indexed";
          doc.version += 1;
          doc.pages = Math.max(doc.pages, 1);
          doc.chunks = 6;
          doc.citation = { page: 1, paragraph: "1.1", text: "已用可读取的示例版本生成模拟引用；实际引用需来自已解析的文档版本。" };
          doc.updated = "刚刚";
          commit(S, U, "已用可读取的示例版本模拟重试，并创建新的演示索引版本。");
        }
        return true;
      case "sec-knowledge-citation":
        doc = documentById(library, id);
        if (doc) openCitation(U, doc);
        return true;
      case "sec-index-open":
        openIndexModal(U, library);
        return true;
      case "sec-index-rebuild-start":
        captureModalDraft(buttonElement, state);
        var generationName = valueFrom(buttonElement, "indexGeneration", library.index.candidateDraft || "").trim();
        if (!generationName) {
          library.index.candidateDraft = "";
          commit(S, U, "", false);
          toast(U, "请填写候选索引代次名称。");
          return true;
        }
        if (generationName === library.index.currentGeneration) {
          library.index.candidateDraft = generationName;
          commit(S, U, "", false);
          toast(U, "候选代次需要与当前代次不同。");
          return true;
        }
        library.index.candidateGeneration = generationName;
        library.index.rebuildStatus = "building";
        library.index.candidateDraft = "";
        library.index.candidateDraftSet = false;
        closeModal(U);
        commit(S, U, "已启动演示重建；旧索引仍继续可用。");
        openIndexModal(U, library);
        return true;
      case "sec-index-rebuild-complete":
        if (library.index.rebuildStatus === "building" && library.index.candidateGeneration) {
          library.index.previousGeneration = library.index.currentGeneration;
          library.index.currentGeneration = library.index.candidateGeneration;
          library.index.candidateGeneration = "";
          library.index.rebuildStatus = "idle";
          settings.retrieval.generation = library.index.currentGeneration;
          closeModal(U);
          commit(S, U, "演示索引代次已切换；旧代次保留为上一个版本。");
        }
        return true;
      case "sec-memory-select":
        captureMemoryDraft(library, buttonElement);
        library.selectedMemoryId = id;
        library.editingMemoryId = null;
        commit(S, U);
        return true;
      case "sec-memory-edit":
        library.selectedMemoryId = id || library.selectedMemoryId;
        library.editingMemoryId = library.selectedMemoryId;
        commit(S, U);
        return true;
      case "sec-memory-cancel":
        captureMemoryDraft(library, buttonElement);
        library.editingMemoryId = null;
        commit(S, U);
        return true;
      case "sec-memory-save":
        memory = memoryById(library, library.editingMemoryId || library.selectedMemoryId);
        var corrected = valueFrom(buttonElement, "memoryText", "").trim();
        if (!memory || !corrected) {
          captureMemoryDraft(library, buttonElement);
          commit(S, U, "", false);
          toast(U, "记忆正文不能为空。");
          return true;
        }
        memory.history.push({ version: memory.version, label: "被新版本替代" });
        memory.version += 1;
        memory.content = corrected;
        memory.status = "Active";
        memory.updated = "刚刚";
        library.editingMemoryId = null;
        library.memoryDraft = null;
        commit(S, U, "已保存为新版本，旧版本关系已保留。");
        return true;
      case "sec-memory-share-preview":
        memory = memoryById(library, id);
        if (memory && memory.status !== "Deleted") {
          var inScopeCount = team.members.filter(function (memberItem) { return memberItem.status !== "archived"; }).length;
          openMemoryShare(U, memory, inScopeCount);
        }
        return true;
      case "sec-memory-candidate-preview":
        item = library.candidates.find(function (candidate) { return candidate.id === id; });
        if (item) openMemoryCandidate(U, library, item);
        return true;
      case "sec-memory-candidate-publish":
        item = library.candidates.find(function (candidate) { return candidate.id === library.pendingCandidateId; });
        if (!item) {
          toast(U, "该候选已不存在，请刷新后重试。");
          return true;
        }
        memory = {
          id: idWithSerial("memory"),
          title: item.title,
          kind: item.kind || "experience",
          scopeType: item.scopeType || "project",
          scopeLabel: item.scopeLabel || "项目：秋季产品验证",
          source: item.source || "记忆候选",
          sourceRef: item.sourceRef || "原型候选记录",
          version: 1,
          status: "Active",
          recall: "Warm",
          content: item.content,
          uses: 0,
          usedIn: [],
          updated: "刚刚",
          history: []
        };
        library.memories.unshift(memory);
        library.selectedMemoryId = memory.id;
        library.pendingCandidateId = null;
        library.candidates = library.candidates.filter(function (candidate) { return candidate.id !== item.id; });
        closeModal(U);
        commit(S, U, "候选已发布为一条新的演示记忆。");
        return true;
      case "sec-memory-candidate-reject":
        item = library.candidates.find(function (candidate) { return candidate.id === library.pendingCandidateId; });
        if (item) library.candidates = library.candidates.filter(function (candidate) { return candidate.id !== item.id; });
        library.pendingCandidateId = null;
        closeModal(U);
        commit(S, U, "已拒绝该记忆候选。");
        return true;
      case "sec-memory-share-confirm":
        memory = memoryById(library, library.selectedMemoryId);
        if (memory && memory.status !== "Deleted") {
          memory.history.push({ version: memory.version, label: "共享范围变更前版本" });
          memory.version += 1;
          memory.scopeType = "workspace";
          memory.scopeLabel = "整个工作区（共享）";
          memory.updated = "刚刚";
          closeModal(U);
          commit(S, U, "已模拟发布共享版本。");
        }
        return true;
      case "sec-memory-archive":
        memory = memoryById(library, id);
        if (memory && memory.status !== "Deleted") {
          memory.recall = memory.recall === "Cold" ? "Warm" : "Cold";
          memory.updated = "刚刚";
          commit(S, U, memory.recall === "Cold" ? "已归档到冷记忆；仍可搜索。" : "已恢复为常用记忆。");
        }
        return true;
      case "sec-memory-forget":
        memory = memoryById(library, id);
        if (memory && memory.status !== "Deleted") {
          openForget(U, library, memory);
          commit(S, U, "", false);
        }
        return true;
      case "sec-memory-forget-confirm":
        memory = memoryById(library, library.pendingForgetId);
        var scopes = [];
        if (typeof document !== "undefined") {
          scopes = Array.prototype.slice.call(document.querySelectorAll("[data-forget-scope]:checked")).map(function (element) { return element.value; });
        }
        if (!memory || !scopes.length) {
          toast(U, "至少选择一个需要清理的副本范围。");
          return true;
        }
        library.forgetLog.push({ id: memory.id, scope: scopes, inFlight: "TASK-236 文本不可撤回；受影响续接需重建上下文", at: "刚刚" });
        memory.content = null;
        memory.status = "Deleted";
        memory.recall = "Cold";
        memory.updated = "刚刚";
        memory.scopeLabel = "已遗忘";
        library.pendingForgetId = null;
        library.forgetDraft = null;
        closeModal(U);
        commit(S, U, "已停止未来注入，并清理所选演示副本范围。");
        return true;
      case "sec-memory-use":
        memory = memoryById(library, id);
        if (memory && memory.status !== "Deleted") {
          memory.uses += 1;
          memory.usedIn.unshift("原型模拟任务 · 刚刚");
          memory.usedIn = memory.usedIn.slice(0, 5);
          commit(S, U, "已在本地记录一次模拟引用。");
        }
        return true;
      case "sec-settings-tab":
        captureSettingsDraft(settings, buttonElement);
        settings.tab = settingTabs.some(function (tab) { return tab[0] === id; }) ? id : "models";
        commit(S, U);
        return true;
      case "sec-model-save":
        var modelSaved = modelDraft(buttonElement, settings.model);
        settings.model.protocol = modelSaved.protocol;
        settings.model.baseUrl = modelSaved.baseUrl;
        settings.model.model = modelSaved.model;
        var modelSecretValue = inputValue(buttonElement, "modelSecret");
        if (modelSecretValue) {
          settings.model.keyConfigured = true;
        }
        modelSecretDraft = "";
        setInputValue(buttonElement, "modelSecret", "");
        clearSettingsPartDraft(settings, "models", "model");
        commit(S, U, "模型配置已保存在本地演示状态。");
        return true;
      case "sec-model-test":
        var modelTest = modelDraft(buttonElement, settings.model);
        settings.model.protocol = modelTest.protocol;
        settings.model.baseUrl = modelTest.baseUrl;
        settings.model.model = modelTest.model;
        draftFor(settings, "models").model = modelTest;
        if (typeof document !== "undefined") {
          var modelSecretElement = document.querySelector("[name='modelSecret']");
          if (modelSecretElement) modelSecretDraft = modelSecretElement.value;
        }
        var modelKeyPresent = !!modelSecretDraft || settings.model.keyConfigured;
        settings.model.testedAt = "刚刚";
        if (!modelTest.baseUrl || !modelTest.model || !modelKeyPresent || /fail|invalid/i.test(modelTest.baseUrl + " " + modelTest.model + " " + modelSecretDraft)) {
          settings.model.result = "模拟测试失败：检查地址、模型名或密钥";
          commit(S, U, "模拟连接失败；表单输入已保留。");
        } else {
          settings.model.keyConfigured = true;
          settings.model.result = "原型模拟通过";
          modelSecretDraft = "";
          setInputValue(buttonElement, "modelSecret", "");
          clearSettingsPartDraft(settings, "models", "model");
          commit(S, U, "原型连接测试通过；未发送真实网络请求。");
        }
        return true;
      case "sec-model-clear-key":
        settings.model.keyConfigured = false;
        modelSecretDraft = "";
        commit(S, U, "已清除密钥配置状态。");
        return true;
      case "sec-retrieval-save":
        var retrievalSaved = retrievalDraft(buttonElement, settings.retrieval);
        settings.retrieval.baseUrl = retrievalSaved.baseUrl;
        settings.retrieval.model = retrievalSaved.model;
        settings.retrieval.dimensions = retrievalSaved.dimensions;
        var retrievalSecretValue = inputValue(buttonElement, "retrievalSecret");
        if (retrievalSecretValue) {
          settings.retrieval.keyConfigured = true;
        }
        retrievalSecretDraft = "";
        setInputValue(buttonElement, "retrievalSecret", "");
        clearSettingsPartDraft(settings, "models", "retrieval");
        commit(S, U, "知识检索模型配置已保存。");
        return true;
      case "sec-retrieval-test":
        var retrievalTest = retrievalDraft(buttonElement, settings.retrieval);
        settings.retrieval.baseUrl = retrievalTest.baseUrl;
        settings.retrieval.model = retrievalTest.model;
        settings.retrieval.dimensions = retrievalTest.dimensions;
        draftFor(settings, "models").retrieval = retrievalTest;
        if (typeof document !== "undefined") {
          var retrievalSecretElement = document.querySelector("[name='retrievalSecret']");
          if (retrievalSecretElement) retrievalSecretDraft = retrievalSecretElement.value;
        }
        if (!retrievalTest.baseUrl || !retrievalTest.model || Number(retrievalTest.dimensions) < 1 || (!retrievalSecretDraft && !settings.retrieval.keyConfigured) || /fail|invalid/i.test(retrievalTest.baseUrl + " " + retrievalTest.model + " " + retrievalSecretDraft)) {
          settings.retrieval.result = "模拟测试失败：检查地址、模型名、密钥或维度";
          commit(S, U, "模拟检索模型测试失败；表单输入已保留。");
        } else {
          settings.retrieval.keyConfigured = true;
          settings.retrieval.result = "原型模拟通过";
          setInputValue(buttonElement, "retrievalSecret", "");
          retrievalSecretDraft = "";
          clearSettingsPartDraft(settings, "models", "retrieval");
          commit(S, U, "原型检索模型测试通过；未发送真实 API 请求。");
        }
        return true;
      case "sec-tool-install":
        item = toolById(settings, id);
        if (item) {
          item.installed = true;
          item.authorized = false;
          commit(S, U, "已模拟安装；尚未授予运行权限。");
        }
        return true;
      case "sec-tool-toggle":
        item = toolById(settings, id);
        if (!item) return true;
        if (!item.installed) {
          toast(U, "请先安装连接或插件。");
          return true;
        }
        item.scope = inputValue(buttonElement, "toolScope") || item.scope;
        item.authorized = !item.authorized;
        commit(S, U, item.authorized ? "已按所选范围分配权限。" : "已撤销工具权限。");
        return true;
      case "sec-tool-scope":
        item = toolById(settings, id);
        if (item) {
          item.scope = inputValue(buttonElement, "toolScope") || item.scope;
          commit(S, U, "工具权限范围已保存。");
        }
        return true;
      case "sec-email-save":
        Object.assign(settings.email, emailDraft(buttonElement, settings.email));
        if (inputValue(buttonElement, "smtpSecret")) {
          settings.email.keyConfigured = true;
        }
        smtpSecretDraft = "";
        setInputValue(buttonElement, "smtpSecret", "");
        clearSettingsPartDraft(settings, "email", "email");
        commit(S, U, "邮箱配置已保存在本地演示状态。");
        return true;
      case "sec-email-test":
        Object.assign(settings.email, emailDraft(buttonElement, settings.email));
        if (typeof document !== "undefined") {
          var smtpSecretElement = document.querySelector("[name='smtpSecret']");
          if (smtpSecretElement) smtpSecretDraft = smtpSecretElement.value;
        }
        if (!settings.email.host || !settings.email.account || !settings.email.from || (!smtpSecretDraft && !settings.email.keyConfigured) || /fail|invalid/i.test(settings.email.host + " " + settings.email.account + " " + smtpSecretDraft)) {
          settings.email.result = "模拟测试失败：检查主机、账号或密钥";
          clearSettingsPartDraft(settings, "email", "email");
          commit(S, U, "模拟连接失败；表单输入已保留。");
        } else {
          settings.email.keyConfigured = true;
          settings.email.result = "原型模拟通过";
          setInputValue(buttonElement, "smtpSecret", "");
          smtpSecretDraft = "";
          clearSettingsPartDraft(settings, "email", "email");
          commit(S, U, "邮箱连接测试通过；未连接 SMTP，也未发信。");
        }
        return true;
      case "sec-email-send-confirm":
        pendingEmailRecipient = emailDraft(buttonElement, settings.email).to;
        confirm(U, "确认测试邮件目标", "原型邮件预览将发给：" + e(U, pendingEmailRecipient) + "。确认后只记录一次模拟发送状态，不会连接 SMTP 或发出邮件。", "模拟试发", "sec-email-send", "primary");
        return true;
      case "sec-email-send":
        settings.email.lastPreview = "已模拟试发至 " + (pendingEmailRecipient || settings.email.to) + " · 仅记录在本地，未发送真实邮件。";
        pendingEmailRecipient = "";
        closeModal(U);
        commit(S, U, "已记录模拟试发结果；没有发送真实邮件。");
        return true;
      case "sec-email-contact-add":
        captureSettingsDraft(settings, buttonElement);
        var contactName = valueFrom(buttonElement, "contactName", "").trim();
        var contactEmail = valueFrom(buttonElement, "contactEmail", "").trim();
        if (!contactName || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(contactEmail)) {
          commit(S, U, "", false);
          toast(U, "请填写联系人姓名和有效邮箱地址。");
          return true;
        }
        if (settings.email.contacts.some(function (contact) { return contact.email.toLowerCase() === contactEmail.toLowerCase(); })) {
          commit(S, U, "", false);
          toast(U, "通讯录中已有这个邮箱地址。");
          return true;
        }
        settings.email.contacts.push({ id: idWithSerial("contact"), name: contactName, email: contactEmail });
        setInputValue(buttonElement, "contactName", "");
        setInputValue(buttonElement, "contactEmail", "");
        if (settings.drafts.email) {
          delete settings.drafts.email.contactName;
          delete settings.drafts.email.contactEmail;
          if (!settings.drafts.email.email && !Object.keys(settings.drafts.email).length) delete settings.drafts.email;
        }
        commit(S, U, "联系人已添加到本地演示通讯录。");
        return true;
      case "sec-email-contact-default":
        captureSettingsDraft(settings, buttonElement);
        item = settings.email.contacts.find(function (contact) { return contact.id === id; });
        if (item) {
          settings.email.to = item.email;
          draftFor(settings, "email").email = Object.assign({}, emailDraft(buttonElement, settings.email), { to: item.email });
          setInputValue(buttonElement, "smtpTo", item.email);
          commit(S, U, "已更新默认收件人。");
        }
        return true;
      case "sec-budget-save":
        settings.budget.monthly = valueFrom(buttonElement, "budgetMonthly", settings.budget.monthly);
        settings.budget.tokens = valueFrom(buttonElement, "budgetTokens", settings.budget.tokens);
        settings.budget.calls = valueFrom(buttonElement, "budgetCalls", settings.budget.calls);
        settings.budget.concurrency = valueFrom(buttonElement, "budgetConcurrency", settings.budget.concurrency);
        settings.budget.duration = valueFrom(buttonElement, "budgetDuration", settings.budget.duration);
        clearSettingsPartDraft(settings, "budget", "budget");
        commit(S, U, "预算上限已保存。");
        return true;
      case "sec-automation-save":
        settings.automation.enabled = inputChecked(buttonElement, "[name='automationEnabled']");
        settings.automation.schedule = valueFrom(buttonElement, "automationSchedule", settings.automation.schedule);
        settings.automation.timezone = valueFrom(buttonElement, "automationTimezone", settings.automation.timezone);
        settings.automation.missed = valueFrom(buttonElement, "automationMissed", settings.automation.missed);
        settings.automation.catchUp = valueFrom(buttonElement, "automationCatchUp", settings.automation.catchUp);
        settings.automation.next = "2026-10-15 " + settings.automation.schedule + " · " + settings.automation.timezone;
        clearSettingsPartDraft(settings, "automation", "automation");
        commit(S, U, "自动化规则与时区已保存；漏跑策略为“" + settings.automation.missed + "”。");
        return true;
      case "sec-automation-run":
        if (!settings.automation.enabled) {
          settings.automation.lastRun = "已暂停，未补跑。";
        } else if (settings.automation.missed === "skip") {
          settings.automation.lastRun = "原型模拟：漏跑已跳过。";
        } else if (settings.automation.missed === "catch-up") {
          settings.automation.lastRun = "原型模拟：按上限补跑 " + Math.min(Number(settings.automation.catchUp) || 0, 1) + " 次。";
        } else {
          settings.automation.lastRun = "原型模拟：漏跑已合并到下一次计划。";
        }
        commit(S, U, settings.automation.lastRun);
        return true;
      case "sec-backup-create":
        settings.backup.backups.unshift({
          id: idWithSerial("backup"),
          name: "手动备份 · 刚刚",
          keyAvailable: true,
          status: "原型模拟可恢复",
          path: "/backups/manual-demo"
        });
        settings.backup.lastRestore = "";
        commit(S, U, "已新增演示备份记录；未复制真实文件。");
        return true;
      case "sec-backup-restore-open":
        item = settings.backup.backups.find(function (backupItem) { return backupItem.id === id; });
        if (item) openBackupRestore(U, settings.backup, item);
        return true;
      case "sec-backup-settings-save":
        settings.backup.schedule = valueFrom(buttonElement, "backupSchedule", settings.backup.schedule);
        settings.backup.retention = valueFrom(buttonElement, "backupRetention", settings.backup.retention);
        clearSettingsDraft(settings, "backup");
        commit(S, U, "备份周期与保留数量已保存。");
        return true;
      case "sec-backup-diagnostics-preview":
        openBackupDiagnostics(U, settings.backup);
        return true;
      case "sec-backup-restore":
        item = settings.backup.backups.find(function (backupItem) { return backupItem.id === id; });
        var restoreDirectory = inputValue(buttonElement, "restoreDirectory").trim();
        if (!restoreDirectory) {
          settings.backup.restoreDirectoryDraft = inputValue(buttonElement, "restoreDirectory");
          settings.backup.restoreDirectoryDraftSet = true;
          commit(S, U, "", false);
          toast(U, "请填写新的恢复目录。");
          return true;
        }
        if (restoreDirectory === settings.backup.workspace) {
          settings.backup.restoreDirectoryDraft = restoreDirectory;
          settings.backup.restoreDirectoryDraftSet = true;
          commit(S, U, "", false);
          toast(U, "恢复目录必须是新的目录，不能覆盖当前工作区。");
          return true;
        }
        if (!item || !item.keyAvailable) {
          settings.backup.restoreDirectoryDraft = restoreDirectory;
          settings.backup.restoreDirectoryDraftSet = true;
          settings.backup.lastRestore = "恢复被阻止：备份 " + (item ? item.name : "") + " 缺少恢复密钥。未覆盖任何目录。";
          closeModal(U);
          commit(S, U, "缺少恢复密钥，恢复未开始。");
          return true;
        }
        settings.backup.lastRestore = "原型模拟恢复完成：" + item.name + " → " + restoreDirectory + "。没有复制实际文件。";
        settings.backup.restoreDirectoryDraft = "";
        settings.backup.restoreDirectoryDraftSet = false;
        closeModal(U);
        commit(S, U, "已记录新目录恢复结果。");
        return true;
      case "sec-access-credential-create":
        captureSettingsDraft(settings, buttonElement);
        var credentialScope = valueFrom(buttonElement, "apiScope", "current-task");
        settings.access.credentials.unshift({
          id: idWithSerial("credential"),
          label: "受限凭据记录",
          scope: credentialScope,
          scopeLabel: permissionScopeLabel(credentialScope) + "：" + permissionTarget(S, credentialScope),
          status: "有效（模拟）",
          createdAt: "刚刚"
        });
        commit(S, U, "已生成受限凭据模拟记录；未生成或保存令牌。");
        return true;
      case "sec-access-credential-revoke":
        item = settings.access.credentials.find(function (credential) { return credential.id === id; });
        if (item && item.status === "有效（模拟）") {
          item.status = "已撤销（模拟）";
          commit(S, U, "模拟凭据记录已撤销。");
        }
        return true;
      case "sec-access-webhook-save":
        captureSettingsDraft(settings, buttonElement);
        var accessDraft = draftFor(settings, "access");
        settings.access.webhook.enabled = accessDraft.webhookEnabled === true;
        settings.access.webhook.scope = accessDraft.webhookScope || settings.access.webhook.scope;
        settings.access.webhook.status = settings.access.webhook.enabled ?
          "已启用 · " + permissionScopeLabel(settings.access.webhook.scope) + "：" + permissionTarget(S, settings.access.webhook.scope) + "（模拟）" : "未启用";
        delete accessDraft.webhookEnabled;
        delete accessDraft.webhookScope;
        if (!Object.keys(accessDraft).length) clearSettingsDraft(settings, "access");
        commit(S, U, "Webhook 范围已保存到本地模拟状态。");
        return true;
      case "sec-access-recovery":
        captureSettingsDraft(settings, buttonElement);
        var recoveryCode = valueFrom(buttonElement, "recoveryCode", "").trim();
        var recoverySessionName = valueFrom(buttonElement, "recoverySessionName", "恢复后的浏览器").trim();
        if (recoveryCode !== "RECOVERY-DEMO" || !recoverySessionName) {
          commit(S, U, "", false);
          toast(U, recoveryCode !== "RECOVERY-DEMO" ? "恢复码无效；原型示例码为 RECOVERY-DEMO。" : "请填写新会话名称。");
          return true;
        }
        settings.access.loggedIn = true;
        settings.access.paired = true;
        settings.access.sessions.forEach(function (session) { session.current = false; });
        settings.access.sessions.unshift({
          id: idWithSerial("session"),
          name: recoverySessionName,
          device: "部署恢复 · 原型会话",
          current: true,
          status: "有效",
          lastSeen: "刚刚"
        });
        settings.access.recoverySessions += 1;
        if (settings.drafts.access) {
          delete settings.drafts.access.recoverySessionName;
          if (!Object.keys(settings.drafts.access).length) clearSettingsDraft(settings, "access");
        }
        commit(S, U, "已模拟通过部署恢复入口建立新会话；未修改密码。");
        return true;
      case "sec-access-pair":
        settings.access.pairingCode = Math.floor(100000 + Math.random() * 900000).toString();
        settings.access.pairingExpiry = "10 分钟后";
        commit(S, U, "已生成一次性演示配对码。");
        return true;
      case "sec-access-login":
        var pairingCode = valueFrom(buttonElement, "pairingCodeInput", "").trim();
        if (!settings.access.pairingCode || pairingCode !== settings.access.pairingCode) {
          toast(U, "配对码无效或已过期；请重新生成后再试。");
          return true;
        }
        settings.access.paired = true;
        settings.access.loggedIn = true;
        settings.access.pairingCode = "";
        settings.access.pairingExpiry = "";
        settings.access.sessions.forEach(function (session) { session.current = false; });
        settings.access.sessions.unshift({ id: idWithSerial("session"), name: "新配对浏览器", device: "原型会话", current: true, status: "有效", lastSeen: "刚刚" });
        commit(S, U, "已模拟配对登录。");
        return true;
      case "sec-access-logout":
        settings.access.loggedIn = false;
        settings.access.sessions.forEach(function (session) { if (session.current) session.status = "已退出"; });
        commit(S, U, "已退出当前演示会话。");
        return true;
      case "sec-access-revoke-open":
        item = settings.access.sessions.find(function (session) { return session.id === id; });
        if (item && item.status === "有效") openRevoke(U, settings.access, item);
        return true;
      case "sec-access-revoke-confirm":
        item = settings.access.sessions.find(function (session) { return session.id === settings.access.pendingRevokeId; });
        if (item) {
          item.status = "已撤销";
          if (item.current) settings.access.loggedIn = false;
          settings.access.pendingRevokeId = null;
          closeModal(U);
          commit(S, U, "会话已撤销。");
        }
        return true;
      default:
        return false;
    }
  }

  root.CareerSecondary = {
    render: render,
    handle: handle,
    captureDrafts: captureDrafts
  };
})(window);
