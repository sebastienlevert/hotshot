export const repository = 'https://github.com/sebastienlevert/hotshot';
export const groups = ['Start here', 'Capture & create', 'Organize & configure', 'AI & coding agents', 'Help & development'];

export const documents = [
  { slug: 'getting-started', title: 'Install & take your first capture', group: groups[0],
    description: 'Pick the right Windows download, find the tray icon, capture, and paste.',
    source: 'README.md', sections: ['Install Hotshot', 'Your first screenshot'] },
  { slug: 'settings', title: 'Settings & shortcuts', group: groups[0],
    description: 'Find settings, customize shortcuts, and make Hotshot work your way.',
    source: 'README.md', sections: ['Set it up your way', 'Shortcuts and tray controls'] },
  { slug: 'screenshots', title: 'Screenshots & scrolling', group: groups[1],
    description: 'Capture regions, unobscured windows, monitors, and guided scrolling content.',
    source: 'README.md', sections: ['Capture screenshots'] },
  { slug: 'editor', title: 'Edit, redact & export', group: groups[1],
    description: 'Annotate a screenshot, protect sensitive pixels, and export a polished copy.',
    source: 'README.md', sections: [{ heading: 'Edit, organize, and compare', intro: true }, 'Edit and export'] },
  { slug: 'recording', title: 'Video & GIFs', group: groups[1],
    description: 'Record with optional audio, pause, trim, extract frames, and make GIFs.',
    source: 'README.md', sections: ['Record video and make GIFs'] },
  { slug: 'library', title: 'Library, pinning & comparisons', group: groups[2],
    description: 'Find captures, use projects and baselines, compare changes, and back up safely.',
    source: 'README.md', sections: ['Find and organize captures', 'Pin, compare, and protect your library'] },
  { slug: 'file-naming', title: 'Folders & filename tokens', group: groups[2],
    description: 'Choose a capture folder and build clear, collision-safe naming patterns.',
    source: 'README.md', sections: ['Choose folders and filenames'] },
  { slug: 'onedrive', title: 'OneDrive settings sync', group: groups[2],
    description: 'Share portable preferences across PCs without sharing device paths or permissions.',
    source: 'README.md', sections: ['Keep settings across machines with OneDrive'] },
  { slug: 'updates', title: 'Updates & local data', group: groups[2],
    description: 'Understand automatic updates, idle restarts, and where your data lives.',
    source: 'README.md', sections: ['Updates and local data'] },
  { slug: 'ai', title: 'Copilot descriptions & local OCR', group: groups[3],
    description: 'Make images searchable with local text recognition or opt-in cloud descriptions.',
    source: 'README.md', sections: ['Search screenshots by description'] },
  { slug: 'agents', title: 'Connect a coding agent', group: groups[3],
    description: 'Set up MCP, plugins, skills, permissions, timed grants, and native approvals.',
    source: 'agents/README.md', sections: ['Enable and connect', 'Plugins and marketplace', 'Tools', 'Limitations and troubleshooting'],
    introduction: 'Hotshot connects a local stdio MCP companion to the running Windows app. It does not expose an HTTP listener, arbitrary file reader, shell executor, or mouse/keyboard automation.' },
  { slug: 'privacy', title: 'Privacy & permissions', group: groups[3],
    description: 'Know what stays local, what an integration can receive, and what redaction does.',
    source: 'README.md', sections: ['Know what leaves your machine'],
    introduction: 'Cloud descriptions, live agent access, and capture-history access are separate choices. They are off by default and are not enabled on another PC by settings sync.\n\n**Redaction does not erase private originals or editable sidecars.** Share flattened exports, not a whole app-data folder or a backup containing originals. See [editing and redaction](../editor/).' },
  { slug: 'troubleshooting', title: 'Troubleshooting & limits', group: groups[4],
    description: 'Solve shortcut, capture, OCR, agent, sync, and update problems.',
    source: 'README.md', sections: ['Troubleshooting', 'Current boundaries'] },
  { slug: 'development', title: 'Build & run from source', group: groups[4],
    description: 'Windows prerequisites, x64/ARM64 builds, and safe development launches.',
    source: 'CONTRIBUTING.md', sections: ['Development prerequisites', 'Get the source and build'] },
  { slug: 'architecture', title: 'Architecture & conventions', group: groups[4],
    description: 'Project boundaries, native UI, physical pixels, persistence, and permission invariants.',
    source: 'CONTRIBUTING.md', sections: ['Repository map', 'Design and implementation rules'] },
  { slug: 'testing', title: 'Testing & native harnesses', group: groups[4],
    description: 'Choose unit tests and integration checks, and understand their side effects.',
    source: 'CONTRIBUTING.md', sections: ['Test the change you made'] },
  { slug: 'website', title: 'Website & documentation development', group: groups[4],
    description: 'Maintain the static marketing site, source-backed guides, search, and GitHub Pages deployment.',
    source: 'CONTRIBUTING.md', sections: ['Website and documentation'] },
  { slug: 'agent-development', title: 'Agent integration development', group: groups[4],
    description: 'Build the companion and exercise manifests, protocol, and native permissions.',
    source: 'agents/README.md', sections: ['Development'] },
  { slug: 'contributing', title: 'Contribute & report bugs', group: groups[4],
    description: 'Propose changes, prepare a focused pull request, and protect private information.',
    source: 'CONTRIBUTING.md', sections: ['Before you start', 'Submit a pull request', 'License and provenance'] },
  { slug: 'releases', title: 'Packaging & releases', group: groups[4],
    description: 'Create packages and understand the complete draft-to-published release process.',
    source: 'CONTRIBUTING.md', sections: ['Packaging and releases'] },
  { slug: 'changelog', title: 'Changelog', group: groups[4],
    description: 'See the shipped features and fixes, directly from the release history.',
    source: 'CHANGELOG.md' },
];

export function section(markdown, selection) {
  markdown = markdown.replace(/\r\n?/g, '\n');
  const heading = typeof selection === 'string' ? selection : selection.heading;
  const headings = [...markdown.matchAll(/^(#{1,6}) (.+)\r?$/gm)];
  const index = headings.findIndex(match => match[2] === heading);
  if (index < 0) throw new Error(`Documentation source heading is missing: ${heading}`);
  const match = headings[index];
  const end = headings.slice(index + 1).find(next =>
    (typeof selection !== 'string' && selection.intro) || next[1].length <= match[1].length)?.index ?? markdown.length;
  const body = markdown.slice(match.index, end).trim();
  return typeof selection !== 'string' && selection.intro
    ? body.slice(body.indexOf('\n') + 1).trim() : body;
}

export function slugify(text) {
  return text.toLowerCase().replace(/<[^>]*>/g, '').replace(/[^\p{L}\p{N}\s-]/gu, '')
    .trim().replace(/\s+/g, '-');
}
