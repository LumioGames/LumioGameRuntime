# 0002 · generated-contract 的生成与校验锚定架构源已提交对象的**原始字节**,drift 闸门拆成「完整性」与「同步度」两条

- 日期:2026-08-29
- 状态:生效

## 背景

`eng/generate-contracts.*` 原先用 `git -C $LUMIO_ARCHITECTURE_ROOT rev-parse HEAD` 取一个 commit 戳进 manifest,却从该仓的**工作区**实际生成;`eng/verify-generated-contracts.*` 同样从工作区重新生成再逐文件比对。架构源仓被多个会话并发编辑,工作区常年带着未提交改动,于是闸门结论取决于「什么时候跑」。实测该仓发布 identity 一天内变了四次,`origin/main` 在单次会话内前移三次(`c350ec6` → `73348dd` → `ee03da9` → `0338c86`)。

曾有一次尝试(commit `4dfc00e`「重新生成 manifest,修正虚假的架构源 provenance」)在 10 分钟后被 `66a71b0` revert。

**第一轮诊断认定「manifest 的五个溯源戳全是假的」,这个判断经审查被证伪。** 实测:架构源 `.gitattributes` 第一行是 `* text=auto`,而 `git archive` 会按**调用方**的 `core.autocrlf` / `core.eol` 做行尾转换。把 `5f06822` 用 `core.autocrlf=true` 导出再跑生成器,拿到的正是 manifest 里那组「假值」:

| 戳 | LF 导出 | CRLF 导出 | 原 manifest 记录 |
|---|---|---|---|
| `schemaRegistrySha256` | `7c2501b5…` | `d5e9a3bb…` | `d5e9a3bb…` ← CRLF |
| `idRegistrySha256` | `0514df6d…` | `9a850363…` | `9a850363…` ← CRLF |
| `fixtureRegistrySha256` | `ef16b5ce…` | `6120e167…` | `6120e167…` ← CRLF |
| `inputHash` | `054e9586…` | `84a2b4c8…` | `84a2b4c8…` ← CRLF |
| `compilerHash` | `3b4230a3…` | `f38136eb…` | `99a786e7…` ← **两者都不是** |

**五个戳里四个是同一个 commit 在 Windows / `autocrlf=true` 上的忠实渲染,不是伪造。** 只有 `compilerHash` 两种渲染都对不上——`tools/lumio_generate.py` 的 `compiler_hash()` 哈希的是两个 `.py` 文件的原始字节,所以唯独它是「读了脏工作区」的产物。

真正的根因因此是两层,且第二层比第一层更隐蔽:

1. 闸门从**可变的工作区**生成(解释 `compilerHash`);
2. 闸门哈希的是**经 attribute 转换后物化的字节**,而不是 git 对象本身 —— 于是即使锚定了 commit,判据仍是 `(commit, 机器的行尾配置)` 的函数,而不是 `commit` 的函数。**只修第一层,就只是把移动靶的轴从时间换成机器**,Windows 上跑同一个干净树会报 drift,照提示重生成又把值翻回去,`4dfc00e` → `66a71b0` 那次翻烧饼会换个轴重演。

## 决策

**一、生成与校验锚定已提交对象,并锁死导出字节。**

- 锚点解析优先级:`LUMIO_ARCHITECTURE_COMMIT`(精确 SHA)> `LUMIO_ARCHITECTURE_REF`(默认 `origin/main`);解析不出 40 位 SHA 即 `ARCHITECTURE_COMMIT_MISSING` / exit `31`,**不回落到工作区**。
- 用 `git archive` 只读物化,**不用 `git worktree add`**:后者会往架构源仓的 `.git` 写 worktree 注册记录,而那个仓正被并发编辑。
- **`git archive` 必须带 `-c core.autocrlf=false -c core.eol=lf -c core.attributesfile=<空文件>`。** 这不是可选项,是本决策成立的前提。三个开关各堵一条会让导出字节随机器变化的通道:前两个堵配置层;第三个堵全局 `~/.gitattributes` —— **属性优先于配置**,一行 `* eol=crlf` 能把前两个 flag 直接压过去(实测:敌意 attributesfile 在场时不加该 flag 得 `d5e9a3bb…`,加上后回到 `7c2501b5…`)。加上三个开关后,导出结果与 `git cat-file blob 5f06822:schemas/index.json` 的原始字节一致。
- **pathspec 限定为 `tools schemas ids fixtures`**(生成器实际读取的全部路径):全树导出带 42 个符号链接,Windows 的 `tar` 在未开 Developer Mode 时无法创建符号链接并非零退出,闸门在默认 Windows 上会直接不可用;限定后为 0 个,且实测产出与全树导出完全一致。

**二、drift 闸门拆成两条性质不同的检查,不合并。**

| 检查 | 性质 | 判据 |
|---|---|---|
| 生成物完整性 | **硬 gate**,决定退出码 | 生成物 == 从 manifest 记录的 commit 重新生成的结果 → 证明「生成物未被手改 + provenance 属实」 |
| 上游同步度 | **纯报告**,永不影响退出码 | manifest 记录的 commit 与上游当前发布之间的 git 事实差距 |

同步度进一步区分**契约面**(`schemas/` `ids/` `fixtures/` 有变更 → `UPSTREAM_CONTRACT_AHEAD`)与**工具面**(仅 `tools/` 变更 → `UPSTREAM_GENERATOR_ONLY`):上游改一个生成器脚本和上游改 Schema,严重性完全不同,合并报告会让读的人无法判断要不要跟。

同步度**只做 git 事实比对,不跑第二次生成器**——跑生成器会让报告结论重新取决于「什么时候跑」,那正是本决策要消灭的东西。

**三、manifest 按 LF 口径重算一次。** 选 LF 而不是 CRLF,因为 LF 导出等于 git blob 原始字节,是唯一与平台无关的基准。重算只改五个溯源戳,六个 artifact 的 `.cs` 字节零变化。**这不是「修正伪造」,是把一个平台相关的渲染换成平台无关的原始字节** —— 原值在它被生成的那台机器上是诚实的。

## 后果

- 闸门语义从「与上游同步」诚实地缩小为「生成物未被手改、provenance 属实」。**这不是削弱:读工作区的实现从未真正实现过前者。** 真正的同步度改由报告项承担,并且报得比原来更细。
- 判据从 `(commit, 机器配置, 时刻)` 收窄为 `(commit, attribute 栈)` 的函数。**配置通道已闭合,attribute 通道没有完全闭合** —— 本决策不声称「commit 的纯函数」,那正是本卡要治的病(把「看起来闭合的」写成「已闭合的」)。实测:注入敌意 global 与 system gitconfig(`autocrlf=true` + `eol=crlf`)后硬 gate 输出与普通环境逐字一致。
- **残留的机器相关输入(两个,均为未提交的本机文件)**:
  1. `$GIT_DIR/info/attributes` —— **没有任何 config 开关**,在架构源仓的 `.git/info/` 里放一份就能改变导出字节,`-c core.attributesfile` 压不住它。
  2. `filter` 的 smudge 命令 —— `git archive` 会应用 smudge,而命令本身来自本机 config。当前架构源 `.gitattributes` 未声明任何 `filter`,所以此通道暂时不活跃。

  要彻底闭合,三个 registry 戳需改为**直接哈希 blob**(`git cat-file blob <commit>:<path>`),绕开整条 attribute 转换链。触发上述两条需要非默认的本机文件,与 `core.autocrlf=true`(Git for Windows 安装器默认)这种「一定会碰到」差一个量级,故本轮不做,记为已知残留面。
- 上游前进不再打断本仓构建。跟进升级成为一次**显式**动作(改 `LUMIO_ARCHITECTURE_REF` 或直接传 commit 后重生成),而不是某次跑闸门时的隐式副作用。代价:锚点前推需要人主动做,长期不推会静默落后——所以同步度报告必须一直保留,不能因为「反正不 fail」而删掉。
- 校验每次要 `git archive` 一棵子树,比直接读工作区慢;换来的是可复现与可复核。
- **快照本身是新的信任点。** 落地时已核实:架构源仓无 `.gitmodules`;`.gitattributes` 有 `* text=auto` 但无 `export-ignore` / `export-subst` / `filter` / `ident` / `working-tree-encoding`;限定 pathspec 后导出 0 个符号链接,四个目录 212 个文件与 `git ls-tree -r` 逐条一致且逐字节等于对应 blob。

  **复核这个信任点要查两样,只查一样会漏:**
  1. **文件清单**——若上游给这些路径加 `export-ignore`,快照会静默少文件。
  2. **`git show <commit>:.gitattributes` 的哈希**——`.gitattributes` 因 pathspec 限定而**不在**那 212 个文件里,却**仍然作用于导出**。上游给它加一行 `eol=` 或 `filter=`,文件清单一条不差、导出字节却变了,只比清单完全测不出来。
- `compilerHash` 随上游 `tools/**` 变动而变,这是它的设计意图(它就是生成器版本戳),不属于契约面变更;同步度报告把这类前进单独标成 `UPSTREAM_GENERATOR_ONLY` 正是为此。
