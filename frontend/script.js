const doc = document;
let intervalId;

function poll() {
    intervalId = setInterval(sendMsg(), 1000);
}

function sendMsg() {
    console.log("Called");
    const el = doc.getElementById('number');
    el.innerHTML += 1;
}