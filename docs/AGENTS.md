# Comprehensive Multi-Agent Architecture (FaceCheckin Project)
> **Core Principle:** Centralized orchestration. Agents do not communicate directly with each other without routing through the Orchestrator.
> *Note: This file enforces strict ANSI compatibility (pure ASCII characters).*

---

## 1. System Core: Orchestrator (Antigravity)
**Role:** Project Manager (PM) + Tech Lead.
**Workflow:**
1. Receive Epic/Feature from User.
2. Formulate `PLAN.md` (break down into phases). 
   * **Persistence Rule:** Orchestrator MUST write current state to `PLAN.md` and `todo.md` before delegating, preventing complete system failure if the orchestrator crashes.
3. Delegate tasks to the correct Sub-Agent via a standardized `CONTEXT.md` handoff.
4. Integrate results based on Output Contracts and enforce Definition of Done (DoD).
5. Update `todo.md`.
**Constraint:** The Orchestrator does not write production code unprompted to protect the overarching context.

---

## 2. Agent Roster & Authorized Domains

### Group 1: Architecture & Global Logic (Gemini 3.1 Pro & Claude Code)
* **Role (Gemini 3.1 Pro - CLI Agent):** Global Visionary & Deep Reasoner. (Installed via `npm install -g @google/gemini-cli`)
  * **Responsibilities:** Ingest massive codebases (1M tokens), analyze videos/images, and perform cross-repo architectural planning using multi-tiered reasoning.
* **Role (Claude Code - MCP):** Fast-Execution Senior Engineer.
  * **Responsibilities:** Scaffold specific modules, write API endpoints, generate Unit Tests, and execute large multi-file refactors with high precision.
* **Domain:** `src/`, `tests/`, systemic architectural changes.

### Group 2: Surgical Fixer & DevOps (Codex CLI - Parallel Terminal)
* **Role:** DevOps / Bug Fixer.
* **Responsibilities:** Resolve complex algorithmic bugs, bash scripting, CI/CD, hardware configurations.
* **Domain:** Scoped context defined by the Orchestrator's `CONTEXT.md`. Default maximum of 5 files for bug fixes, expandable to 8 files for CI/CD or infrastructure refactors.

### Group 3: Research (Perplexity Sonar-Pro - MCP)
* **Role:** Technical Researcher.
* **Responsibilities:** Fetch latest library endpoints, parse lengthy third-party API documentation.
* **Domain:** Read-only web access. Strict prohibition against editing local code files.

### Group 4: Advisory & UX (Claude Web / Gemini / ChatGPT - Web Automation)
* **Role:** Rapid Prototyper / UX Consultant / UI Design Expert.
* **Responsibilities:** Analyze REDACTED logs for patterns, draft user-facing text, brainstorm UI flows, design complex UI Toolkit components, and advise on UX patterns.
* **Domain:** Markdown documentation, UI text, UI/UX structure.

---

## 3. Communication Protocols & Security

* **MCP Direct Link:** Orchestrator natively delegates to Claude Code and Perplexity.
* **The Single Source of Truth (SSoT):** When querying Codex CLI, the Orchestrator must generate a `CONTEXT.md` file detailing the bug, affected scope (1-3 files), and constraints.
* **Web Security Guardrail:** Before executing Web Automation to prompt Gemini/ChatGPT, the Orchestrator MUST redact all API keys, database credentials, and proprietary PII.

---

## 4. Definition of Done (DoD) & Acceptance Criteria

The Orchestrator will only accept a sub-agent's work if it passes the following checklist:
1. **Tests Passing:** Unit/Integration tests succeed.
2. **Linting:** Zero warnings/errors from language-appropriate linters (e.g., `black`/`flake8` for Python, Unity Compiler / `.editorconfig` for C#).
3. **No Regressions:** Core smoke tests pass.
4. **Output Contracts Met:** Appropriate artifacts (e.g., `PATCH.diff`, `TEST_REPORT.md`) are provided.
5. **Rollback Condition:** Delegated tasks MUST be executed on isolated task branches (e.g., `agent/{group}-{task-id}`) where possible to prevent conflict. Rollback entails deleting the task branch and reverting merge commits, ensuring safe and atomic reversions.

---

## 5. Fallback Protocols

**Situation A: Sub-Agent Token Exhaustion / Automation Failure**
* Action: **Graceful Pause**.
* The Orchestrator stops active file modification immediately. Instead of executing a risky `git stash`, the system will generate a manual `.diff` patch file and a `EMERGENCY_STATE.md` snapshot text file documenting the uncompleted lines. Control is then yielded back to the User.

**Situation B: MCP / API Connectivity Drops**
* Action: **Switch to Manual Handoff**.
* The Orchestrator prepares `.txt` instructions and requests the User to manually paste them into the surviving terminal/browser interfaces until connectivity is restored.

**Situation C: API Capacity Limits / Model Exhaustion**
* Action: **Automatic Model Fallback**.
* If a primary "preview" or high-tier model (e.g., `gemini-3.1-pro-preview`) returns a "No capacity available" or rate-limit error, the Orchestrator MUST immediately switch the agent to a stable fallback model (e.g., `gemini-3.1-flash` hoặc `gemini-2.5-flash`). This ensures development continuity without forcing the User to wait.

---

## 6. Output Contracts & Handoffs

To eliminate ambiguity during the "Integrate Results" phase (Step 4), interactions follow strict artifact contracts:
* **The Handoff (Orchestrator -> Agent):** Orchestrator generates a `CONTEXT.md` containing: Task ID, explicit Affected Files list, Constraints (e.g., no-touch zones), related context (PLAN.md), and Expected Output.
* **Code/Fix Agents (Grps 1 & 2):** Must output committed changes on an isolated task branch, or a standard `PATCH.diff` file, plus a `TEST_REPORT.md` if tests were involved.
* **Research Agents (Grp 3):** Must output `RESEARCH.md` detailing sources, findings, and technical recommendations.
* **UX/Advisory Agents (Grp 4):** Must output standard Markdown files detailing UI workflows or drafted text.
