#!/usr/bin/env bash
set -euo pipefail

# Generate build-layer SBOM evidence. Optional arguments are project paths.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$root/artifacts/sbom"
mkdir -p "$output"

node - "$root" "$output" "$@" <<'NODE'
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const cp = require('child_process');

const [rootArg, outputArg, ...explicitProjects] = process.argv.slice(2);
const root = path.resolve(rootArg);
const output = path.resolve(outputArg);

function walk(directory, result) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (['bin', 'obj', 'artifacts', '.git', 'node_modules'].includes(entry.name)) continue;
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) walk(full, result);
    else if (/\.(csproj|fsproj|vbproj)$/i.test(entry.name)) result.push(full);
  }
}

const configured = process.env.LUMIO_DEPENDENCY_PROJECTS
  ? process.env.LUMIO_DEPENDENCY_PROJECTS.split(path.delimiter).filter(Boolean)
  : [];
const requested = [...explicitProjects, ...configured];
let projects;
if (requested.length) projects = [...new Set(requested.map((value) => path.resolve(root, value)))];
else { projects = []; walk(root, projects); projects.sort(); }

function parseJsonOutput(outputText) {
  const start = outputText.indexOf('{');
  if (start < 0) throw new Error('dotnet did not return JSON');
  return JSON.parse(outputText.slice(start));
}

function graphFor(project) {
  const command = process.env.DOTNET || 'dotnet';
  const args = ['list', project, 'package', '--include-transitive', '--format', 'json'];
  return parseJsonOutput(cp.execFileSync(command, args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
}

function collectGraph(node, result) {
  if (!node || typeof node !== 'object') return;
  if (!Array.isArray(node) && typeof node.id === 'string' && (node.resolvedVersion || node.version)) {
    result.push({ id: node.id, version: node.resolvedVersion || node.version, contentHash: node.contentHash || null });
  }
  for (const value of Object.values(node)) collectGraph(value, result);
}

function packageCache(id, version) {
  const roots = [
    process.env.NUGET_PACKAGES,
    process.env.USERPROFILE && path.join(process.env.USERPROFILE, '.nuget', 'packages'),
    process.env.HOME && path.join(process.env.HOME, '.nuget', 'packages')
  ].filter(Boolean);
  for (const base of roots) {
    const directory = path.join(base, id.toLowerCase(), version.toLowerCase());
    if (!fs.existsSync(directory)) continue;
    const entries = fs.readdirSync(directory);
    const nupkg = entries.find((entry) => entry.toLowerCase().endsWith('.nupkg'));
    const nuspec = entries.find((entry) => entry.toLowerCase().endsWith('.nuspec'));
    return { nupkg: nupkg ? path.join(directory, nupkg) : null, nuspec: nuspec ? path.join(directory, nuspec) : null };
  }
  return { nupkg: null, nuspec: null };
}

function licenseFromNuspec(file) {
  if (!file || !fs.existsSync(file)) return null;
  const xml = fs.readFileSync(file, 'utf8');
  const match = xml.match(/<license\b([^>]*)>([\s\S]*?)<\/license>/i);
  if (!match) return null;
  const type = (match[1].match(/\btype\s*=\s*"([^"]*)"/i) || [])[1];
  return type && type.toLowerCase() === 'expression' ? match[2].trim() : null;
}

function packageEvidence(record) {
  const cache = packageCache(record.id, record.version);
  const license = licenseFromNuspec(cache.nuspec);
  const packageHash = cache.nupkg && fs.existsSync(cache.nupkg)
    ? crypto.createHash('sha256').update(fs.readFileSync(cache.nupkg)).digest('hex')
    : null;
  return { ...record, license, packageHash };
}

let toolVersion = process.env.LUMIO_SBOM_TOOL_VERSION || 'wrapper-1.0';
try {
  const command = process.env.DOTNET || 'dotnet';
  toolVersion = process.env.LUMIO_SBOM_TOOL_VERSION || cp.execFileSync(command, ['--version'], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
} catch (_) { /* SDK is not required for the empty-project baseline. */ }

const componentMap = new Map();
const projectGraph = [];
for (const project of projects) {
  const relative = path.relative(root, project).replaceAll(path.sep, '/');
  let graph;
  try { graph = graphFor(project); } catch (error) {
    process.stderr.write(`SBOM_GRAPH_UNAVAILABLE project=${relative} message=${error.message}\n`);
    process.exit(32);
  }
  const packages = [];
  collectGraph(graph, packages);
  const unique = new Map();
  for (const record of packages) unique.set(`${record.id.toLowerCase()}@${record.version}`, record);
  const packageIds = [];
  for (const record of unique.values()) {
    const key = `${record.id.toLowerCase()}@${record.version}`;
    packageIds.push(record.id);
    if (!componentMap.has(key)) componentMap.set(key, packageEvidence(record));
  }
  projectGraph.push({ project: relative, packages: packageIds.sort() });
}

const components = [...componentMap.values()].sort((a, b) => `${a.id}@${a.version}`.localeCompare(`${b.id}@${b.version}`)).map((record) => {
  const component = {
    type: 'library',
    name: record.id,
    version: record.version,
    purl: `pkg:nuget/${encodeURIComponent(record.id)}@${record.version}`,
    properties: [
      { name: 'lumio:contentHash', value: record.contentHash || '' },
      { name: 'lumio:packageHash', value: record.packageHash || '' }
    ]
  };
  if (record.license) component.licenses = [{ license: { id: record.license } }];
  return component;
});
const evidence = { toolName: 'Lumio SBOM wrapper', toolVersion, packageHashes: Object.fromEntries([...componentMap.values()].map((record) => [`${record.id}@${record.version}`, record.packageHash])), projectGraph };
const digest = crypto.createHash('sha256').update(JSON.stringify(evidence)).digest('hex');
const bom = {
  bomFormat: 'CycloneDX',
  specVersion: '1.5',
  serialNumber: `urn:uuid:${digest.slice(0, 8)}-${digest.slice(8, 12)}-5${digest.slice(13, 16)}-8${digest.slice(17, 20)}-${digest.slice(20, 32)}`,
  version: 1,
  metadata: { tools: [{ vendor: 'LumioGames', name: evidence.toolName, version: toolVersion }], properties: [{ name: 'lumio:projectGraph', value: JSON.stringify(projectGraph) }] },
  components
};
fs.writeFileSync(path.join(output, 'bom.json'), `${JSON.stringify(bom, null, 2)}\n`, 'utf8');
fs.writeFileSync(path.join(output, 'sbom-manifest.json'), `${JSON.stringify({ ...evidence, generatedAtUtc: new Date().toISOString(), bomSha256: crypto.createHash('sha256').update(JSON.stringify(bom)).digest('hex') }, null, 2)}\n`, 'utf8');
process.stdout.write(`SBOM_OK output=artifacts/sbom projects=${projects.length} packages=${components.length}\n`);
NODE
