const ICONS = {
  download: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5"/><path d="M12 15V3"/></svg>',
  shield: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 13c0 5-3.5 7.5-7.7 8.9a1 1 0 0 1-.6 0C7.5 20.5 4 18 4 13V6a1 1 0 0 1 .7-1l7-2a1 1 0 0 1 .6 0l7 2a1 1 0 0 1 .7 1z"/><path d="m9 12 2 2 4-4"/></svg>',
  package: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m7.5 4.3 9 5.2"/><path d="M21 8a2 2 0 0 0-1-1.7l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.7l7 4a2 2 0 0 0 2 0l7-4a2 2 0 0 0 1-1.7Z"/><path d="m3.3 7 8.7 5 8.7-5"/><path d="M12 22V12"/></svg>',
  hash: '<svg viewBox="0 0 24 24" aria-hidden="true"><line x1="4" x2="20" y1="9" y2="9"/><line x1="4" x2="20" y1="15" y2="15"/><line x1="10" x2="8" y1="3" y2="21"/><line x1="16" x2="14" y1="3" y2="21"/></svg>',
  copy: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect width="14" height="14" x="8" y="8" rx="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg>',
  badge: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3.9 7.7a2 2 0 0 1 .9-2.4l6.2-3a2 2 0 0 1 1.8 0l6.2 3a2 2 0 0 1 .9 2.4L17.5 14a6 6 0 0 1-11 0z"/><path d="m9 12 2 2 4-5"/></svg>',
  'file-check': '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="m9 15 2 2 4-4"/></svg>',
  monitor: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect width="20" height="14" x="2" y="3" rx="2"/><line x1="8" x2="16" y1="21" y2="21"/><line x1="12" x2="12" y1="17" y2="21"/></svg>',
  search: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/></svg>',
  database: '<svg viewBox="0 0 24 24" aria-hidden="true"><ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5"/><path d="M3 12c0 1.7 4 3 9 3s9-1.3 9-3"/></svg>',
  settings: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12.2 2h-.4a2 2 0 0 0-2 1.7l-.1.7a2 2 0 0 1-3 1.3l-.6-.4a2 2 0 0 0-2.6.3l-.3.3a2 2 0 0 0-.3 2.6l.4.6a2 2 0 0 1-1.3 3l-.7.1a2 2 0 0 0-1.7 2v.4a2 2 0 0 0 1.7 2l.7.1a2 2 0 0 1 1.3 3l-.4.6a2 2 0 0 0 .3 2.6l.3.3a2 2 0 0 0 2.6.3l.6-.4a2 2 0 0 1 3 1.3l.1.7a2 2 0 0 0 2 1.7h.4a2 2 0 0 0 2-1.7l.1-.7a2 2 0 0 1 3-1.3l.6.4a2 2 0 0 0 2.6-.3l.3-.3a2 2 0 0 0 .3-2.6l-.4-.6a2 2 0 0 1 1.3-3l.7-.1a2 2 0 0 0 1.7-2v-.4a2 2 0 0 0-1.7-2l-.7-.1a2 2 0 0 1-1.3-3l.4-.6a2 2 0 0 0-.3-2.6l-.3-.3a2 2 0 0 0-2.6-.3l-.6.4a2 2 0 0 1-3-1.3l-.1-.7a2 2 0 0 0-2-1.7Z"/><circle cx="12" cy="12" r="3"/></svg>'
};

document.querySelectorAll('[data-icon]').forEach((node) => {
  node.innerHTML = ICONS[node.dataset.icon] || '';
});

const $ = (id) => document.getElementById(id);

function showToast(message) {
  const toast = $('toast');
  toast.textContent = message;
  toast.classList.add('show');
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => toast.classList.remove('show'), 2200);
}

function filenameFromUrl(url) {
  try {
    return new URL(url, window.location.origin).pathname.split('/').pop();
  } catch {
    return 'TLAHStudioSetup.exe';
  }
}

async function copyText(value) {
  await navigator.clipboard.writeText(value);
  showToast('Copied');
}

async function loadRelease() {
  const response = await fetch('/api/latest', { cache: 'no-store' });
  if (!response.ok) throw new Error(`Release API returned ${response.status}`);
  const data = await response.json();
  const installerName = data.installerFile || filenameFromUrl(data.installerUrl);
  const downloadUrl = data.downloadUrl || `/files/${installerName}`;

  $('release-title').textContent = `Version ${data.version}`;
  $('platform').textContent = `${data.platform || 'windows'} ${data.arch || 'x64'}`.replace('windows', 'Windows');
  $('packageSize').textContent = data.sizeLabel || 'Available';
  $('updatedAt').textContent = data.updatedAtLabel || 'Current';
  $('sha256').textContent = data.sha256 || 'Unavailable';
  $('signerSubject').textContent = data.signer?.subject || 'Beijing Ke Entropy Technology certificate';
  $('signerThumbprint').textContent = data.signer?.thumbprint || 'F6DC173C746447A05FF83B9F7162121344CC09F0';
  $('hashCommand').textContent = `Get-FileHash .\\${installerName} -Algorithm SHA256`;
  $('signatureCommand').textContent = `Get-AuthenticodeSignature .\\${installerName} | Format-List`;

  for (const id of ['downloadButton', 'heroDownload']) {
    const link = $(id);
    link.href = downloadUrl;
    link.setAttribute('download', installerName);
  }

  $('manifestLink').href = data.manifestUrl || '/files/latest.json';
  $('manifestSigLink').href = data.signatureUrl || '/files/latest.json.sig';
}

document.addEventListener('click', async (event) => {
  const button = event.target.closest('[data-copy-target]');
  if (!button) return;
  const target = $(button.dataset.copyTarget);
  if (!target) return;

  try {
    await copyText(target.textContent.trim());
  } catch {
    showToast('Copy failed');
  }
});

loadRelease().catch((error) => {
  console.error(error);
  $('release-title').textContent = 'Release unavailable';
  $('packageSize').textContent = 'Try again later';
  showToast('Release information unavailable');
});
