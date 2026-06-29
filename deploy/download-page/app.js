const ICONS = {
  download: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5"/><path d="M12 15V3"/></svg>',
  shield: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M20 13c0 5-3.5 7.5-7.7 8.9a1 1 0 0 1-.6 0C7.5 20.5 4 18 4 13V6a1 1 0 0 1 .7-1l7-2a1 1 0 0 1 .6 0l7 2a1 1 0 0 1 .7 1z"/><path d="m9 12 2 2 4-4"/></svg>',
  package: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m7.5 4.3 9 5.2"/><path d="M21 8a2 2 0 0 0-1-1.7l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.7l7 4a2 2 0 0 0 2 0l7-4a2 2 0 0 0 1-1.7Z"/><path d="m3.3 7 8.7 5 8.7-5"/><path d="M12 22V12"/></svg>',
  hash: '<svg viewBox="0 0 24 24" aria-hidden="true"><line x1="4" x2="20" y1="9" y2="9"/><line x1="4" x2="20" y1="15" y2="15"/><line x1="10" x2="8" y1="3" y2="21"/><line x1="16" x2="14" y1="3" y2="21"/></svg>',
  copy: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect width="14" height="14" x="8" y="8" rx="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg>',
  badge: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3.9 7.7a2 2 0 0 1 .9-2.4l6.2-3a2 2 0 0 1 1.8 0l6.2 3a2 2 0 0 1 .9 2.4L17.5 14a6 6 0 0 1-11 0z"/><path d="m9 12 2 2 4-5"/></svg>',
  'file-check': '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="m9 15 2 2 4-4"/></svg>',
  terminal: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m4 17 6-6-6-6"/><path d="M12 19h8"/></svg>',
  code: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m16 18 6-6-6-6"/><path d="m8 6-6 6 6 6"/></svg>',
  lock: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect width="18" height="11" x="3" y="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>',
  layers: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m12 2 9 5-9 5-9-5Z"/><path d="m3 12 9 5 9-5"/><path d="m3 17 9 5 9-5"/></svg>'
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
  showToast('已复制');
}

function setText(id, value) {
  const node = $(id);
  if (node) node.textContent = value;
}

async function loadRelease() {
  const response = await fetch('/api/latest', { cache: 'no-store' });
  if (!response.ok) throw new Error(`Release API returned ${response.status}`);
  const data = await response.json();
  const installerName = data.installerFile || filenameFromUrl(data.installerUrl);
  const downloadUrl = data.downloadUrl || `/files/${installerName}`;
  const manifestUrl = data.manifestUrl || '/tlah/windows/latest.json';
  const signatureUrl = data.signatureUrl || '/tlah/windows/latest.json.sig';

  setText('download-title', `Version ${data.version}`);
  setText('platform', `${data.platform || 'windows'} ${data.arch || 'x64'}`.replace('windows', 'Windows'));
  setText('packageSize', data.sizeLabel || 'Available');
  setText('updatedAt', data.updatedAtLabel || 'Current');
  setText('sha256', data.sha256 || 'Unavailable');
  setText('signerSubject', data.signer?.subject || 'Beijing Ke Entropy Technology certificate');
  setText('signerThumbprint', data.signer?.thumbprint || 'F6DC173C746447A05FF83B9F7162121344CC09F0');
  setText('hashCommand', `Get-FileHash .\\${installerName} -Algorithm SHA256`);
  setText('signatureCommand', `Get-AuthenticodeSignature .\\${installerName} | Format-List`);
  setText('releaseNotes', data.releaseNotes || 'No release notes available.');
  setText('surfaceVersion', `v${data.version}`);

  for (const id of ['downloadButton', 'heroDownload']) {
    const link = $(id);
    if (!link) continue;
    link.href = downloadUrl;
    link.setAttribute('download', installerName);
  }

  $('manifestLink').href = manifestUrl;
  $('manifestSigLink').href = signatureUrl;
  $('signatureTextLink').href = signatureUrl;
}

document.addEventListener('click', async (event) => {
  const button = event.target.closest('[data-copy-target]');
  if (!button) return;
  const target = $(button.dataset.copyTarget);
  if (!target) return;

  try {
    await copyText(target.textContent.trim());
  } catch {
    showToast('复制失败');
  }
});

loadRelease().catch((error) => {
  console.error(error);
  setText('download-title', '版本信息不可用');
  setText('packageSize', '请稍后重试');
  showToast('无法读取发布信息');
});
