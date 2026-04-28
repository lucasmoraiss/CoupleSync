import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { Octokit } from "@octokit/rest";
import { z } from "zod";
import * as YAML from "yaml";
import * as path from "node:path";

// --- Configuration -----------------------------------------------------------

const GITHUB_TOKEN = process.env.GITHUB_TOKEN;
const GITHUB_OWNER = process.env.GITHUB_OWNER;
const GITHUB_REPO = process.env.GITHUB_REPO;

if (!GITHUB_TOKEN) {
  console.error("GITHUB_TOKEN environment variable is required.");
  process.exit(1);
}
if (!GITHUB_OWNER) {
  console.error("GITHUB_OWNER environment variable is required.");
  process.exit(1);
}
if (!GITHUB_REPO) {
  console.error("GITHUB_REPO environment variable is required.");
  process.exit(1);
}

const octokit = new Octokit({ auth: GITHUB_TOKEN });
const owner = GITHUB_OWNER;
const repo = GITHUB_REPO;

// --- MCP Server Setup --------------------------------------------------------

const server = new McpServer({
  name: "github-actions-mcp",
  version: "1.0.0",
});

// --- Tool: list_workflows ----------------------------------------------------

server.tool(
  "list_workflows",
  "List all GitHub Actions workflows in the repository",
  {},
  async () => {
    const { data } = await octokit.actions.listRepoWorkflows({ owner, repo });
    const workflows = data.workflows.map((w) => ({
      id: w.id,
      name: w.name,
      state: w.state,
      path: w.path,
      badge_url: w.badge_url,
      html_url: w.html_url,
    }));
    return { content: [{ type: "text", text: JSON.stringify(workflows, null, 2) }] };
  }
);

// --- Tool: list_workflow_runs ------------------------------------------------

server.tool(
  "list_workflow_runs",
  "List recent runs for a specific workflow (by name or ID). Returns up to 10 most recent runs with status, conclusion, branch, and timing info.",
  {
    workflow: z.string().describe("Workflow filename (e.g. 'ci.yml') or numeric workflow ID"),
    branch: z.string().optional().describe("Filter by branch name"),
    status: z
      .enum(["queued", "in_progress", "completed", "waiting", "requested", "pending"])
      .optional()
      .describe("Filter by run status"),
    per_page: z.number().min(1).max(100).default(10).describe("Number of results (max 100)"),
  },
  async ({ workflow, branch, status, per_page }) => {
    const workflowId = isNaN(Number(workflow)) ? workflow : Number(workflow);
    const params: Parameters<typeof octokit.actions.listWorkflowRuns>[0] = {
      owner,
      repo,
      workflow_id: workflowId,
      per_page,
    };
    if (branch) params.branch = branch;
    if (status) params.status = status as any;

    const { data } = await octokit.actions.listWorkflowRuns(params);
    const runs = data.workflow_runs.map((r) => ({
      id: r.id,
      name: r.name,
      status: r.status,
      conclusion: r.conclusion,
      branch: r.head_branch,
      event: r.event,
      created_at: r.created_at,
      updated_at: r.updated_at,
      html_url: r.html_url,
      run_attempt: r.run_attempt,
      run_number: r.run_number,
    }));
    return {
      content: [
        {
          type: "text",
          text: `Total runs: ${data.total_count}\n\n${JSON.stringify(runs, null, 2)}`,
        },
      ],
    };
  }
);

// --- Tool: get_workflow_run --------------------------------------------------

server.tool(
  "get_workflow_run",
  "Get detailed information about a specific workflow run by its run ID",
  {
    run_id: z.number().describe("The workflow run ID"),
  },
  async ({ run_id }) => {
    const { data: run } = await octokit.actions.getWorkflowRun({ owner, repo, run_id });
    const result = {
      id: run.id,
      name: run.name,
      status: run.status,
      conclusion: run.conclusion,
      workflow_id: run.workflow_id,
      branch: run.head_branch,
      sha: run.head_sha,
      event: run.event,
      actor: run.actor?.login,
      created_at: run.created_at,
      updated_at: run.updated_at,
      run_started_at: run.run_started_at,
      run_attempt: run.run_attempt,
      run_number: run.run_number,
      html_url: run.html_url,
      jobs_url: run.jobs_url,
      logs_url: run.logs_url,
    };
    return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
  }
);

// --- Tool: list_workflow_jobs ------------------------------------------------

server.tool(
  "list_workflow_jobs",
  "List all jobs for a specific workflow run, including step-level status and timing",
  {
    run_id: z.number().describe("The workflow run ID"),
  },
  async ({ run_id }) => {
    const { data } = await octokit.actions.listJobsForWorkflowRun({
      owner,
      repo,
      run_id,
    });
    const jobs = data.jobs.map((j) => ({
      id: j.id,
      name: j.name,
      status: j.status,
      conclusion: j.conclusion,
      started_at: j.started_at,
      completed_at: j.completed_at,
      steps: j.steps?.map((s) => ({
        name: s.name,
        status: s.status,
        conclusion: s.conclusion,
        number: s.number,
        started_at: s.started_at,
        completed_at: s.completed_at,
      })),
    }));
    return { content: [{ type: "text", text: JSON.stringify(jobs, null, 2) }] };
  }
);

// --- Tool: get_job_logs ------------------------------------------------------

server.tool(
  "get_job_logs",
  "Download and return the logs for a specific job in a workflow run. Useful for debugging failed steps.",
  {
    job_id: z.number().describe("The job ID (get from list_workflow_jobs)"),
  },
  async ({ job_id }) => {
    const { data } = await octokit.actions.downloadJobLogsForWorkflowRun({
      owner,
      repo,
      job_id,
    });
    const logs = typeof data === "string" ? data : String(data);
    // Truncate very long logs to avoid overwhelming the context
    const maxLen = 50_000;
    const truncated = logs.length > maxLen ? logs.slice(-maxLen) + "\n\n... (log truncated, showing last 50k chars)" : logs;
    return { content: [{ type: "text", text: truncated }] };
  }
);

// --- Tool: trigger_workflow --------------------------------------------------

server.tool(
  "trigger_workflow",
  "Trigger a workflow_dispatch event to start a new workflow run. The workflow must have 'workflow_dispatch' in its 'on' triggers.",
  {
    workflow: z.string().describe("Workflow filename (e.g. 'deploy.yml') or numeric workflow ID"),
    ref: z.string().default("main").describe("Git ref (branch or tag) to run the workflow on"),
    inputs: z
      .record(z.string(), z.string())
      .optional()
      .describe("Key-value inputs for the workflow (if workflow defines inputs)"),
  },
  async ({ workflow, ref, inputs }) => {
    const workflowId = isNaN(Number(workflow)) ? workflow : Number(workflow);
    await octokit.actions.createWorkflowDispatch({
      owner,
      repo,
      workflow_id: workflowId,
      ref,
      inputs: inputs ?? {},
    });
    return {
      content: [
        {
          type: "text",
          text: `Workflow '${workflow}' triggered successfully on ref '${ref}'.${inputs ? ` Inputs: ${JSON.stringify(inputs)}` : ""}`,
        },
      ],
    };
  }
);

// --- Tool: cancel_workflow_run -----------------------------------------------

server.tool(
  "cancel_workflow_run",
  "Cancel a workflow run that is currently in progress or queued",
  {
    run_id: z.number().describe("The workflow run ID to cancel"),
  },
  async ({ run_id }) => {
    await octokit.actions.cancelWorkflowRun({ owner, repo, run_id });
    return { content: [{ type: "text", text: `Workflow run ${run_id} has been cancelled.` }] };
  }
);

// --- Tool: rerun_workflow ----------------------------------------------------

server.tool(
  "rerun_workflow",
  "Re-run a completed workflow run. Can re-run all jobs or only failed jobs.",
  {
    run_id: z.number().describe("The workflow run ID to re-run"),
    only_failed: z.boolean().default(false).describe("If true, only re-run failed jobs"),
  },
  async ({ run_id, only_failed }) => {
    if (only_failed) {
      await octokit.actions.reRunWorkflowFailedJobs({ owner, repo, run_id });
      return { content: [{ type: "text", text: `Re-running only failed jobs for run ${run_id}.` }] };
    } else {
      await octokit.actions.reRunWorkflow({ owner, repo, run_id });
      return { content: [{ type: "text", text: `Re-running all jobs for run ${run_id}.` }] };
    }
  }
);

// --- Tool: get_workflow_file -------------------------------------------------

server.tool(
  "get_workflow_file",
  "Read the content of a workflow YAML file from the .github/workflows directory",
  {
    filename: z.string().describe("Workflow filename (e.g. 'ci.yml')"),
  },
  async ({ filename }) => {
    // Sanitize: only allow alphanumeric, dashes, underscores, dots
    const sanitized = path.basename(filename);
    if (sanitized !== filename || /[^a-zA-Z0-9._-]/.test(sanitized)) {
      return {
        content: [{ type: "text", text: `Invalid filename: '${filename}'. Only simple filenames like 'ci.yml' are allowed.` }],
        isError: true,
      };
    }
    const { data } = await octokit.repos.getContent({
      owner,
      repo,
      path: `.github/workflows/${sanitized}`,
    });
    if ("content" in data && data.content) {
      const content = Buffer.from(data.content, "base64").toString("utf-8");
      return { content: [{ type: "text", text: content }] };
    }
    return { content: [{ type: "text", text: "Could not read file content." }], isError: true };
  }
);

// --- Tool: validate_workflow_file --------------------------------------------

server.tool(
  "validate_workflow_file",
  "Validate the YAML syntax and basic structure of a GitHub Actions workflow file. Checks for valid YAML, required top-level keys ('name', 'on', 'jobs'), and common structural issues.",
  {
    filename: z.string().describe("Workflow filename (e.g. 'ci.yml')"),
  },
  async ({ filename }) => {
    // Sanitize
    const sanitized = path.basename(filename);
    if (sanitized !== filename || /[^a-zA-Z0-9._-]/.test(sanitized)) {
      return {
        content: [{ type: "text", text: `Invalid filename: '${filename}'.` }],
        isError: true,
      };
    }

    // Fetch the file from GitHub
    const { data } = await octokit.repos.getContent({
      owner,
      repo,
      path: `.github/workflows/${sanitized}`,
    });
    if (!("content" in data) || !data.content) {
      return { content: [{ type: "text", text: "Could not read file content." }], isError: true };
    }
    const content = Buffer.from(data.content, "base64").toString("utf-8");

    const issues: string[] = [];

    // 1. YAML parse
    let parsed: any;
    try {
      parsed = YAML.parse(content);
    } catch (e: any) {
      return {
        content: [
          {
            type: "text",
            text: `YAML Parse Error: ${e.message}\n\nThe file is not valid YAML. Fix the syntax error above.`,
          },
        ],
      };
    }

    if (typeof parsed !== "object" || parsed === null) {
      return {
        content: [{ type: "text", text: "Parsed YAML is not an object. A workflow file must be a YAML mapping." }],
      };
    }

    // 2. Required top-level keys
    if (!parsed.on && !parsed.true) {
      issues.push("Missing required key 'on' — defines what events trigger this workflow.");
    }
    if (!parsed.jobs) {
      issues.push("Missing required key 'jobs' — defines the jobs to run.");
    }
    if (!parsed.name) {
      issues.push("Warning: Missing 'name' key — recommended for identifying the workflow.");
    }

    // 3. Check jobs structure
    if (parsed.jobs && typeof parsed.jobs === "object") {
      for (const [jobName, jobDef] of Object.entries(parsed.jobs)) {
        const job = jobDef as any;
        if (!job || typeof job !== "object") {
          issues.push(`Job '${jobName}': must be a mapping.`);
          continue;
        }
        if (!job["runs-on"]) {
          issues.push(`Job '${jobName}': missing 'runs-on' key.`);
        }
        if (!job.steps && !job.uses) {
          issues.push(`Job '${jobName}': missing 'steps' key (or 'uses' for reusable workflows).`);
        }
        if (job.steps && Array.isArray(job.steps)) {
          for (let i = 0; i < job.steps.length; i++) {
            const step = job.steps[i];
            if (!step.uses && !step.run) {
              issues.push(`Job '${jobName}', step ${i + 1}: must have either 'uses' or 'run'.`);
            }
          }
        }
      }
    }

    // 4. Check triggers
    const triggers = parsed.on || parsed.true;
    if (triggers) {
      const triggerList = typeof triggers === "string" ? [triggers] : Array.isArray(triggers) ? triggers : Object.keys(triggers);
      const validTriggers = [
        "push", "pull_request", "pull_request_target", "workflow_dispatch",
        "workflow_run", "schedule", "release", "create", "delete",
        "deployment", "issues", "issue_comment", "fork", "watch",
        "repository_dispatch", "workflow_call", "merge_group",
      ];
      for (const t of triggerList) {
        if (typeof t === "string" && !validTriggers.includes(t)) {
          issues.push(`Warning: Unknown trigger '${t}'. Check GitHub Actions docs for valid event names.`);
        }
      }
    }

    if (issues.length === 0) {
      return {
        content: [
          {
            type: "text",
            text: `✅ Workflow '${sanitized}' is valid.\n\nYAML syntax: OK\nRequired keys: OK\nJobs structure: OK\nTriggers: OK`,
          },
        ],
      };
    }

    return {
      content: [
        {
          type: "text",
          text: `Validation issues for '${sanitized}':\n\n${issues.map((i, idx) => `${idx + 1}. ${i}`).join("\n")}\n\nYAML syntax: OK`,
        },
      ],
    };
  }
);

// --- Tool: get_workflow_usage ------------------------------------------------

server.tool(
  "get_workflow_usage",
  "Get billing usage information for a specific workflow",
  {
    workflow_id: z.number().describe("The workflow ID (get from list_workflows)"),
  },
  async ({ workflow_id }) => {
    const { data } = await octokit.actions.getWorkflowUsage({
      owner,
      repo,
      workflow_id,
    });
    return { content: [{ type: "text", text: JSON.stringify(data, null, 2) }] };
  }
);

// --- Tool: list_run_artifacts ------------------------------------------------

server.tool(
  "list_run_artifacts",
  "List all artifacts produced by a workflow run",
  {
    run_id: z.number().describe("The workflow run ID"),
  },
  async ({ run_id }) => {
    const { data } = await octokit.actions.listWorkflowRunArtifacts({
      owner,
      repo,
      run_id,
    });
    const artifacts = data.artifacts.map((a) => ({
      id: a.id,
      name: a.name,
      size_in_bytes: a.size_in_bytes,
      created_at: a.created_at,
      expires_at: a.expires_at,
      expired: a.expired,
    }));
    return { content: [{ type: "text", text: JSON.stringify(artifacts, null, 2) }] };
  }
);

// --- Start Server ------------------------------------------------------------

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error("MCP Server failed to start:", err);
  process.exit(1);
});
