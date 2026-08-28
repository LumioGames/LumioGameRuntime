#!/usr/bin/env bash
set -euo pipefail

# Validate the repository package graph. Optional arguments are project paths;
# without arguments all projects below the repository root are discovered.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
policy="$root/eng/dependency-policy.json"
report_dir="$root/artifacts/dependencies"
mkdir -p "$report_dir"

node - "$root" "$policy" "$@" <<'NODE'
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const cp = require('child_process');

const [rootArg, policyPath, ...explicitProjects] = process.argv.slice(2);
const root = path.resolve(rootArg);
const policy = JSON.parse(fs.readFileSync(policyPath, 'utf8'));
const reportPath = path.join(root, 'artifacts', 'dependencies', 'dependency-report.json');
const issues = [];
const projectRecords = [];
const packageRecords = new Map();

function issue(code, message, project) {
  issues.push({ code, message, ...(project ? { project } : {}) });
}

function walk(directory, result) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (['bin', 'obj', 'artifacts', '.git', 'node_modules'].includes(entry.name)) continue;
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) walk(full, result);
    else if (/\.(csproj|fsproj|vbproj)$/i.test(entry.name)) result.push(full);
  }
}

function projectPaths() {
  const configured = process.env.LUMIO_DEPENDENCY_PROJECTS
    ? process.env.LUMIO_DEPENDENCY_PROJECTS.split(path.delimiter).filter(Boolean)
    : [];
  const requested = [...explicitProjects, ...configured];
  if (requested.length) return [...new Set(requested.map((value) => path.resolve(root, value)))];
  const discovered = [];
  walk(root, discovered);
  return discovered.sort();
}

function attrs(text) {
  const values = {};
  for (const match of text.matchAll(/([A-Za-z_:][\w:.-]*)\s*=\s*"([^"]*)"/g)) values[match[1]] = match[2];
  return values;
}

function property(xml, name) {
  const match = xml.match(new RegExp(`<${name}\\b[^>]*>([\\s\\S]*?)</${name}>`, 'i'));
  return match ? match[1].trim() : '';
}

function centralVersions() {
  const xml = fs.readFileSync(path.join(root, 'Directory.Packages.props'), 'utf8');
  const versions = new Map();
  for (const match of xml.matchAll(/<PackageVersion\b([^>]*)\/?>(?:<\/PackageVersion>)?/gi)) {
    const values = attrs(match[1]);
    if (values.Include && values.Version) versions.set(values.Include.toLowerCase(), values.Version);
  }
  return versions;
}

const central = centralVersions();

function floating(version) {
  return !version || !/^\d+\.\d+\.\d+$/.test(version) || /[\[\]\(\),*?]|-[0-9A-Za-z]/.test(version);
}

function regexForGlob(glob) {
  const escaped = glob.replace(/[.+^${}()|[\]\\]/g, '\\$&');
  return new RegExp(`^${escaped.replace(/\*/g, '.*').replace(/\?/g, '.')}$`, 'i');
}

function scopeMatches(name, scopes) {
  return scopes.some((scope) => regexForGlob(scope).test(name));
}

function discoverPackageReferences(xml, projectPath) {
  const references = [];
  for (const match of xml.matchAll(/<PackageReference\b([^>]*)(?:\/>|>[\\s\\S]*?<\/PackageReference>)/gi)) {
    const values = attrs(match[1]);
    const id = values.Include || values.Update;
    if (!id) continue;
    const version = values.Version || values.VersionOverride || '';
    references.push({ id, version, explicit: Boolean(values.Version || values.VersionOverride) });
  }
  const projectName = property(xml, 'AssemblyName') || path.basename(projectPath, path.extname(projectPath));
  return { references, projectName };
}

function collectGraph(node, result) {
  if (!node || typeof node !== 'object') return;
  if (!Array.isArray(node) && typeof node.id === 'string' && (node.resolvedVersion || node.version)) {
    result.push({
      id: node.id,
      version: node.resolvedVersion || node.version,
      requestedVersion: node.requestedVersion || null,
      contentHash: node.contentHash || null
    });
  }
  for (const value of Object.values(node)) collectGraph(value, result);
}

function parseJsonOutput(output) {
  const start = output.indexOf('{');
  if (start < 0) throw new Error('dotnet did not return JSON');
  return JSON.parse(output.slice(start));
}

function dotnetJson(projectPath, extra) {
  const command = process.env.DOTNET || 'dotnet';
  const args = ['list', projectPath, 'package', '--include-transitive', ...extra, '--format', 'json'];
  try {
    return parseJsonOutput(cp.execFileSync(command, args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
  } catch (error) {
    const output = `${error.stdout || ''}`;
    if (output.trim()) {
      try { return parseJsonOutput(output); } catch (_) { /* report below */ }
    }
    throw error;
  }
}

function packageCache(id, version) {
  const roots = [
    process.env.NUGET_PACKAGES,
    process.env.USERPROFILE && path.join(process.env.USERPROFILE, '.nuget', 'packages'),
    process.env.HOME && path.join(process.env.HOME, '.nuget', 'packages')
  ].filter(Boolean);
  const lowerId = id.toLowerCase();
  const lowerVersion = version.toLowerCase();
  for (const base of roots) {
    const directory = path.join(base, lowerId, lowerVersion);
    if (!fs.existsSync(directory)) continue;
    const entries = fs.readdirSync(directory);
    const nuspec = entries.find((entry) => entry.toLowerCase().endsWith('.nuspec'));
    const nupkg = entries.find((entry) => entry.toLowerCase().endsWith('.nupkg'));
    return {
      nuspec: nuspec ? path.join(directory, nuspec) : null,
      nupkg: nupkg ? path.join(directory, nupkg) : null
    };
  }
  return { nuspec: null, nupkg: null };
}

function decodeXml(value) {
  return value.replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").trim();
}

function licenseFromNuspec(file) {
  if (!file || !fs.existsSync(file)) return null;
  const xml = fs.readFileSync(file, 'utf8');
  const match = xml.match(/<license\b([^>]*)>([\s\S]*?)<\/license>/i);
  if (!match) return null;
  const values = attrs(match[1]);
  if ((values.type || '').toLowerCase() !== 'expression') return null;
  return decodeXml(match[2]);
}

function hashFile(file) {
  if (!file || !fs.existsSync(file)) return null;
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

function checkLicense(record) {
  const cache = packageCache(record.id, record.version);
  const license = licenseFromNuspec(cache.nuspec);
  record.license = license;
  record.packageHash = record.packageHash || hashFile(cache.nupkg);
  if (!license) {
    issue('DEPENDENCY_LICENSE_UNKNOWN', `${record.id}@${record.version} has no SPDX license expression`);
    return;
  }
  const tokens = license.split(/\s+(?:OR|AND|WITH)\s+/i).map((value) => value.trim()).filter(Boolean);
  for (const token of tokens) {
    if (policy.requiresLegalReview.some((value) => value.toLowerCase() === token.toLowerCase()) ||
        policy.forbiddenLicensePatterns.some((value) => token.toLowerCase().includes(value.toLowerCase()))) {
      issue('DEPENDENCY_LICENSE_REVIEW_REQUIRED', `${record.id}@${record.version} license=${license}`);
    } else if (!policy.allowedLicenses.some((value) => value.toLowerCase() === token.toLowerCase())) {
      issue('DEPENDENCY_LICENSE_UNKNOWN', `${record.id}@${record.version} license=${license}`);
    }
  }
}

function checkVulnerabilities(graph, projectPath) {
  const vulnerabilities = [];
  function visit(node) {
    if (!node || typeof node !== 'object') return;
    if (Array.isArray(node.vulnerabilities)) vulnerabilities.push(...node.vulnerabilities);
    for (const value of Object.values(node)) visit(value);
  }
  visit(graph);
  for (const vulnerability of vulnerabilities) {
    issue('DEPENDENCY_VULNERABILITY', `${projectPath}: ${JSON.stringify(vulnerability)}`);
  }
}

const projects = projectPaths();
for (const projectPath of projects) {
  if (!fs.existsSync(projectPath)) {
    issue('DEPENDENCY_PROJECT_MISSING', projectPath);
    continue;
  }
  const relative = path.relative(root, projectPath).replaceAll(path.sep, '/');
  const xml = fs.readFileSync(projectPath, 'utf8');
  const { references, projectName } = discoverPackageReferences(xml, projectPath);
  const lock = property(xml, 'NuGetLockFilePath');
  const lockPath = lock ? path.resolve(path.dirname(projectPath), lock) : path.join(path.dirname(projectPath), 'packages.lock.json');
  if (policy.requireLockFiles && !fs.existsSync(lockPath)) issue('DEPENDENCY_LOCK_FILE_MISSING', `${relative}: ${path.relative(root, lockPath)}`, relative);
  for (const reference of references) {
    const centralVersion = central.get(reference.id.toLowerCase());
    if (reference.explicit) {
      if (floating(reference.version)) issue('FLOATING_VERSION_FORBIDDEN', `${reference.id} Version="${reference.version}"`, relative);
      issue('EXPLICIT_VERSION_FORBIDDEN', `${reference.id} must use Directory.Packages.props`, relative);
      if (centralVersion && reference.version !== centralVersion) issue('CENTRAL_VERSION_MISMATCH', `${reference.id} expected ${centralVersion} actual ${reference.version}`, relative);
    } else if (!centralVersion) {
      issue('PACKAGE_VERSION_NOT_CENTRALLY_PINNED', reference.id, relative);
    }
    const scopes = policy.packageScopes[reference.id];
    if (scopes && !scopeMatches(projectName, scopes)) issue('PACKAGE_SCOPE_VIOLATION', `${reference.id} is not allowed in ${projectName}`, relative);
  }
  let graph;
  try {
    graph = dotnetJson(projectPath, []);
  } catch (error) {
    issue('DEPENDENCY_GRAPH_UNAVAILABLE', `${relative}: ${error.message}`, relative);
    projectRecords.push({ path: relative, name: projectName, packageReferences: references.map((reference) => reference.id), packages: [] });
    continue;
  }
  const packages = [];
  collectGraph(graph, packages);
  const unique = new Map();
  for (const record of packages) unique.set(`${record.id.toLowerCase()}@${record.version}`, record);
  for (const record of unique.values()) {
    const scopes = policy.packageScopes[record.id];
    if (scopes && !scopeMatches(projectName, scopes)) issue('PACKAGE_SCOPE_VIOLATION', `${record.id} is not allowed in ${projectName}`, relative);
  }
  for (const record of unique.values()) {
    const key = `${record.id.toLowerCase()}@${record.version}`;
    if (!packageRecords.has(key)) packageRecords.set(key, { id: record.id, version: record.version, contentHash: record.contentHash });
  }
  try {
    checkVulnerabilities(dotnetJson(projectPath, ['--vulnerable']), relative);
  } catch (error) {
    issue('DEPENDENCY_VULNERABILITY_AUDIT_UNAVAILABLE', `${relative}: ${error.message}`, relative);
  }
  projectRecords.push({ path: relative, name: projectName, packageReferences: references.map((reference) => reference.id), packages: [...unique.values()] });
}

for (const record of packageRecords.values()) checkLicense(record);

const report = {
  policy: path.relative(root, policyPath).replaceAll(path.sep, '/'),
  generatedAtUtc: new Date().toISOString(),
  projects: projectRecords,
  packages: [...packageRecords.values()],
  issues,
  status: issues.length ? 'failed' : 'ok'
};
fs.writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');

if (issues.length) {
  for (const entry of issues) process.stderr.write(`${entry.code} ${entry.message}${entry.project ? ` project=${entry.project}` : ''}\n`);
  process.stderr.write(`DEPENDENCY_POLICY_FAILED issues=${issues.length}\n`);
  process.exit(31);
}
process.stdout.write(`DEPENDENCY_POLICY_OK projects=${projects.length}\n`);
NODE
