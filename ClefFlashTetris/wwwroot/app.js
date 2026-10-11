(() => {
  "use strict";

  const W = 10, H = 20, CELL = 30;
  const canvas = document.querySelector("#board");
  const ctx = canvas.getContext("2d");
  const ui = Object.fromEntries(["score", "lines", "pieces", "modelStatus", "statusDot", "decisionLogPath", "toggleAuto", "restart", "restartOverlay", "overlay", "latency", "chosenMove", "confidence", "probabilities", "note"].map(id => [id, document.querySelector(`#${id}`)]));
  const colors = ["", "#7bdaff", "#ffe269", "#ae8bff", "#79ed9b", "#ff727f", "#7195ff", "#ffad62"];
  const shapes = {
    I: [[1,1,1,1]], O: [[2,2],[2,2]], T: [[0,3,0],[3,3,3]],
    S: [[0,4,4],[4,4,0]], Z: [[5,5,0],[0,5,5]],
    J: [[6,0,0],[6,6,6]], L: [[0,0,7],[7,7,7]]
  };

  let board, current, score, lines, pieces, auto = false, busy = false, timer, bag = [];

  const clone = value => value.map(row => [...row]);
  const rotate = matrix => matrix[0].map((_, x) => matrix.map(row => row[x]).reverse());
  const rotations = shape => {
    const result = [];
    let matrix = clone(shape);
    for (let i = 0; i < 4; i++) {
      const key = JSON.stringify(matrix);
      if (!result.some(item => JSON.stringify(item) === key)) result.push(clone(matrix));
      matrix = rotate(matrix);
    }
    return result;
  };

  function nextType() {
    if (!bag.length) bag = Object.keys(shapes).sort(() => Math.random() - .5);
    return bag.pop();
  }

  function spawn() {
    const type = nextType();
    current = { type, matrix: clone(shapes[type]), x: 3, y: 0, rotation: 0 };
    pieces++;
    if (collides(board, current.matrix, current.x, current.y)) return gameOver();
    updateUi();
    draw();
    if (auto) void decideAndPlay();
  }

  function collides(grid, matrix, ox, oy) {
    for (let y = 0; y < matrix.length; y++) for (let x = 0; x < matrix[y].length; x++) {
      if (!matrix[y][x]) continue;
      const gx = ox + x, gy = oy + y;
      if (gx < 0 || gx >= W || gy >= H || (gy >= 0 && grid[gy][gx])) return true;
    }
    return false;
  }

  function dropY(grid, matrix, x) {
    let y = 0;
    if (collides(grid, matrix, x, y)) return null;
    while (!collides(grid, matrix, x, y + 1)) y++;
    return y;
  }

  function merge(grid, matrix, ox, oy) {
    const result = clone(grid);
    matrix.forEach((row, y) => row.forEach((value, x) => { if (value && oy + y >= 0) result[oy + y][ox + x] = value; }));
    return result;
  }

  function clearLines(grid) {
    const kept = grid.filter(row => row.some(cell => !cell));
    const cleared = H - kept.length;
    while (kept.length < H) kept.unshift(Array(W).fill(0));
    return { grid: kept, cleared };
  }

  function metrics(grid) {
    const heights = [], holesByColumn = [];
    for (let x = 0; x < W; x++) {
      let top = H, holes = 0, seen = false;
      for (let y = 0; y < H; y++) {
        if (grid[y][x]) { if (!seen) top = y; seen = true; }
        else if (seen) holes++;
      }
      heights.push(H - top); holesByColumn.push(holes);
    }
    return {
      holes: holesByColumn.reduce((a,b) => a+b, 0),
      aggregateHeight: heights.reduce((a,b) => a+b, 0),
      bumpiness: heights.slice(1).reduce((sum, height, index) => sum + Math.abs(height - heights[index]), 0),
      maxHeight: Math.max(...heights)
    };
  }

  function candidates() {
    const result = [];
    rotations(shapes[current.type]).forEach((matrix, rotation) => {
      for (let x = 0; x <= W - matrix[0].length; x++) {
        const y = dropY(board, matrix, x);
        if (y === null) continue;
        const landed = merge(board, matrix, x, y);
        const cleared = clearLines(landed);
        result.push({ id: `r${rotation}-x${x}`, rotation, x, landingY: y, clearedLines: cleared.cleared, ...metrics(cleared.grid) });
      }
    });
    return result;
  }

  async function decideAndPlay() {
    if (!auto || busy || !current) return;
    busy = true;
    const options = candidates();
    try {
      const response = await fetch("/api/decision", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ board, piece: current.type, score, lines, candidates: options })
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const decision = await response.json();
      const move = options.find(option => option.id === decision.move);
      if (!move) throw new Error("Invalid move");
      showDecision(decision, options);
      const matrix = rotations(shapes[current.type])[move.rotation];
      current = { ...current, matrix, rotation: move.rotation, x: move.x, y: move.landingY };
      draw();
      await new Promise(resolve => setTimeout(resolve, 260));
      lock();
    } catch (error) {
      ui.note.textContent = `Decision failed: ${error.message}`;
      auto = false;
      updateAutoButton();
    } finally {
      busy = false;
    }
  }

  function showDecision(decision, options) {
    const selected = options.find(option => option.id === decision.move);
    ui.latency.textContent = `${decision.latencyMs} ms`;
    ui.chosenMove.textContent = `${decision.move} · ${current.type}`;
    ui.confidence.textContent = `Confidence ${(decision.confidence * 100).toFixed(1)}% · ${decision.source}`;
    ui.note.textContent = decision.note || "";
    ui.probabilities.replaceChildren();
    Object.entries(decision.probabilities).sort((a,b) => b[1] - a[1]).slice(0, 5).forEach(([id, value]) => {
      const row = document.createElement("div"); row.className = "prob-row";
      row.innerHTML = `<span>${id}${id === selected?.id ? " ★" : ""}</span><div class="bar"><i style="width:${value * 100}%"></i></div><span>${(value * 100).toFixed(1)}%</span>`;
      ui.probabilities.append(row);
    });
  }

  function lock() {
    board = merge(board, current.matrix, current.x, current.y);
    const cleared = clearLines(board);
    board = cleared.grid;
    if (cleared.cleared) {
      lines += cleared.cleared;
      score += [0, 100, 300, 500, 800][cleared.cleared];
    } else score += 4;
    current = null;
    setTimeout(spawn, auto ? 120 : 0);
  }

  function stepDown() {
    if (!current || auto || busy) return;
    if (!collides(board, current.matrix, current.x, current.y + 1)) current.y++;
    else lock();
    draw();
  }

  function hardDrop() {
    if (!current || auto || busy) return;
    current.y = dropY(board, current.matrix, current.x);
    lock();
  }

  function move(dx) {
    if (!current || auto || busy || collides(board, current.matrix, current.x + dx, current.y)) return;
    current.x += dx; draw();
  }

  function turn() {
    if (!current || auto || busy) return;
    const matrix = rotate(current.matrix);
    if (!collides(board, matrix, current.x, current.y)) { current.matrix = matrix; draw(); }
  }

  function drawCell(x, y, value, alpha = 1) {
    ctx.globalAlpha = alpha;
    ctx.fillStyle = colors[value]; ctx.fillRect(x * CELL + 1, y * CELL + 1, CELL - 2, CELL - 2);
    ctx.fillStyle = "#fff3"; ctx.fillRect(x * CELL + 3, y * CELL + 3, CELL - 6, 3);
    ctx.globalAlpha = 1;
  }

  function draw() {
    ctx.fillStyle = "#080a10"; ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.strokeStyle = "#171b27"; ctx.lineWidth = 1;
    for (let x = 0; x <= W; x++) { ctx.beginPath(); ctx.moveTo(x*CELL,0); ctx.lineTo(x*CELL,H*CELL); ctx.stroke(); }
    for (let y = 0; y <= H; y++) { ctx.beginPath(); ctx.moveTo(0,y*CELL); ctx.lineTo(W*CELL,y*CELL); ctx.stroke(); }
    board.forEach((row,y) => row.forEach((value,x) => { if (value) drawCell(x,y,value); }));
    if (current) current.matrix.forEach((row,y) => row.forEach((value,x) => { if (value) drawCell(current.x+x,current.y+y,value); }));
  }

  function updateUi() { ui.score.textContent = score; ui.lines.textContent = lines; ui.pieces.textContent = pieces; }
  function updateAutoButton() { ui.toggleAuto.textContent = auto ? "Stop autopilot" : "Start autopilot"; ui.toggleAuto.classList.toggle("primary", !auto); }
  function toggleAuto() { auto = !auto; updateAutoButton(); if (auto) void decideAndPlay(); }
  function gameOver() { current = null; auto = false; busy = false; updateAutoButton(); ui.overlay.classList.remove("hidden"); draw(); }
  function restart() { clearInterval(timer); board = Array.from({length:H}, () => Array(W).fill(0)); score = lines = pieces = 0; current = null; busy = false; bag = []; ui.overlay.classList.add("hidden"); ui.note.textContent = ""; updateUi(); draw(); spawn(); timer = setInterval(stepDown, 650); }

  document.addEventListener("keydown", event => {
    if (["ArrowLeft","ArrowRight","ArrowDown","ArrowUp"," "].includes(event.key)) event.preventDefault();
    if (event.key === "ArrowLeft") move(-1);
    else if (event.key === "ArrowRight") move(1);
    else if (event.key === "ArrowDown") stepDown();
    else if (event.key === "ArrowUp") turn();
    else if (event.key === " ") hardDrop();
    else if (event.key.toLowerCase() === "a") toggleAuto();
  });
  ui.toggleAuto.addEventListener("click", toggleAuto);
  ui.restart.addEventListener("click", restart);
  ui.restartOverlay.addEventListener("click", restart);

  fetch("/api/status").then(response => response.json()).then(status => {
    ui.statusDot.classList.add(status.modelAvailable ? "live" : "fallback");
    ui.decisionLogPath.textContent = status.decisionLog;
    ui.modelStatus.textContent = status.modelAvailable
      ? `Ollama · ${status.model}`
      : status.available
        ? `Ollama is running · install ${status.model}`
        : "Local fallback · start Ollama";
    if (status.note) ui.note.textContent = status.note;
  }).catch(() => { ui.modelStatus.textContent = "Model status unavailable"; });
  restart();
})();
