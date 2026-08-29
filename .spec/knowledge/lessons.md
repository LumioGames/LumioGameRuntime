---
name: lessons
description: 经验教训——reviewer 反复退回的同类问题与 Agent 常犯坑;开工前与复盘沉淀时查
metadata:
  type: doc
  status: 已交付
---

# 经验教训（Lessons Learned）

复发问题的暂存区：记录 reviewer 反复退回的同类问题与 Agent 常犯的坑，让同一个坑不踩第三次。本文档是规范的**候选池**——条目在这里验证价值，稳定后升格，不在这里长住。

## 收录准入

- **同类问题第二次出现才收录**——单次偶发不收，防噪音。
- 来源：reviewer 退回报告、交回物的 known gaps、用户纠偏。
- 不收待办（走任务卡）；不收项目常识（进 `standards/` 或 feature 文档）。

## 条目格式

一条 lesson 一个小节，新条目加在「条目」节最上方（倒序）：

    ### <一句话规避规则>
    - 日期：YYYY-MM-DD
    - 现象：踩了什么坑、复发几次
    - 根因：为什么会发生
    - 规避：怎么做能不再犯（可验证的行为，不是口号）
    - 来源：reviewer 报告 / known gaps / 用户纠偏（附提交或任务标识）

## 升级路径

某条 lesson 被稳定复用（约第三次引用起）→ 升格为 `knowledge/standards/` 规则或 `rules/` 红线，原条目标注「已升格 → <落点>」，保留不删。

## 条目

### 闸门的判据必须锚定不可变对象的**原始字节**，闭合到哪一步就只声称到哪一步，并用对照组探针证明它真的会响

- 日期：2026-08-29
- 现象：`eng/verify-generated-contracts.*` 从架构源仓的**工作区**重新生成再比对，而该仓被多个会话并发编辑，闸门结论随之漂移（实测其发布 identity 一天内变四次，`origin/main` 单次会话内前移三次）。更早一次（`4dfc00e` → 10 分钟后被 `66a71b0` revert）已踩过同一个坑。修复时改为按 commit 用 `git archive` 只读物化——**看似解决了，实则只是把移动靶的轴从「什么时候跑」换成了「在哪台机器上跑」**：架构源 `.gitattributes` 第一行是 `* text=auto`，`git archive` 会按调用方的 `core.autocrlf` 做行尾转换，同一个 commit 在 Windows 默认配置下导出的字节与 Linux/macOS 不同。审查阶段实测：用 `core.autocrlf=true` 导出再跑生成器，拿到的正是被当成「伪造」而修掉的那组旧值——五个溯源戳里**四个是同一 commit 在 Windows 上的忠实渲染，根本不是伪造**（只有 `compilerHash` 是真的读了脏工作区）。差一点就以「修正虚假 provenance」的名义把一组诚实的值改掉，并把错误根因写进 ADR。
- 根因：两层，第二层更隐蔽。① 判据引用了可变来源（他仓工作区）；② 判据哈希的是**经 attribute 转换后物化的字节**，而不是 git 对象本身——于是即使锚定了 commit，判据仍是 `(commit, 机器配置)` 的函数。共同的病理是把「看起来在守护的东西」当成「在守护」，且失效**静默**：闸门照常输出绿色。第三种形态同样在场：`.github/workflows/repository-policy.yml` 至今**没有调用** `eng/verify-dependencies.sh` / `eng/generate-sbom.sh` / `eng/verify-generated-contracts.sh`——脚本写得再全，不在准入路径上就等于不存在。
- 规避：
  1. **判据只引用不可变对象，且要取到它的原始字节**：比对基准是 git 对象/已发布 artifact 的哈希。`git archive` 至少要带 `-c core.autocrlf=false -c core.eol=lf -c core.attributesfile=<空文件>`（**属性优先于配置**，全局 `~/.gitattributes` 一行 `* eol=crlf` 就能把前两个压过去）；验证办法是与 `git cat-file blob <commit>:<path>` 比对。跨仓只读物化用 `git archive` 不用 `git worktree add`（后者会往对方 `.git` 写注册记录）。
  2. **闭合到哪一步，就只声称到哪一步**。上述三个开关堵住的是配置通道；`$GIT_DIR/info/attributes` 没有任何 config 开关，`filter` smudge 也仍会被 archive 应用——所以准确说法是判据为 `(commit, attribute 栈)` 的函数，**不是** `commit` 的纯函数。真要闭合就直接哈希 blob，绕开整条转换链。把「看起来闭合的」写成「已闭合的」，正是本条教训要治的病，写文档时会不自觉地复发。
  3. **锚点解析失败即失败**，不得回落到工作区（`ARCHITECTURE_COMMIT_MISSING` / exit 31）。静默回落是第一层的根。
  4. **硬 gate 与报告项分开**：会随上游变动的判断做成报告、不 fail；只有确定性判据才配决定退出码。参见 [`../decisions/0002-generated-contract-gate-anchors-committed-objects.md`](../decisions/0002-generated-contract-gate-anchors-committed-objects.md)。
  5. **交付前用对照组探针证伪，而且探针要打在「判据本身会不会变」上，不只打在「违规会不会被抓」上**：本次抓住第二层靠的是「注入 `core.autocrlf=true` 再跑一遍，看结论动不动」——只测「改坏了会不会报错」永远发现不了它。**只读配置不算探针**：本次曾 `grep export-ignore` 后判定 `.gitattributes` 无害，却没读它第一行的 `* text=auto`，正是同一种错误的自我复现。
- 来源：R-00131（T03）交付与其 reviewer 退回报告（P0-1）；本仓 `4dfc00e` / `66a71b0` 的先例；总调度跨仓通报（LumioClient / LumioServer 各自独立踩中 BannedApiAnalyzers 因文件名不匹配而整份禁令静默失效）。
