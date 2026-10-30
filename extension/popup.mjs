import {describeView} from './presentation.mjs';
const enabled = document.querySelector('#enabled');
const buttons = [...document.querySelectorAll('button')];

function render(view) {
    const [heading, explanation] = describeView(view);
    enabled.checked = view.enabled;
    document.querySelector('#state').textContent = heading;
    document.querySelector('#explanation').textContent = explanation;
    document.querySelector('#restore').classList.toggle('needed', view.status.restoreRequired);
}

async function command(message) {
    buttons.forEach(button => { button.disabled = true; });
    enabled.disabled = true;
    document.querySelector('#feedback').textContent = '';
    try {
        render(await chrome.runtime.sendMessage(message));
    } catch {
        document.querySelector('#feedback').textContent = '状態を取得できません。もう一度開いて確認してください。';
    } finally {
        buttons.forEach(button => { button.disabled = false; });
        enabled.disabled = false;
    }
}

enabled.addEventListener('change', () => void command({action: 'enable', enabled: enabled.checked}));
for (const action of ['retry', 'diagnose', 'restore']) {
    document.querySelector(`#${action}`).addEventListener('click', () => void command({action}));
}
void command({action: 'view'});
