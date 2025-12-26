const resp = await fetch('http://localhost:333/v1/chat/completions', {
  method: 'POST',
  headers: {'Content-Type': 'application/json'},
  body: JSON.stringify({
    model: 'llama3',
    messages: [{role: 'user', content: 'Explain quantum computing.'}]
  })
});

const reader = resp.body.getReader();
const decoder = new TextDecoder();
while (true) {
  const {done, value} = await reader.read();
  if (done) break;
  const chunk = decoder.decode(value);
  console.log(chunk); // Process SSE lines like "data: {...}"
}