<template>
  <main class="identity-shell">
    <section class="identity-card">
      <p class="identity-eyebrow">Authentication</p>
      <h1>无效的身份</h1>
      <p>当前访问链接缺少有效 key，或该 key 未通过服务端校验，无法继续使用该页面。</p>
    </section>
  </main>
</template>

<style scoped>
.identity-shell {
  min-height: 100vh;
  display: grid;
  place-items: center;
  padding: 24px;
}

.identity-card {
  width: min(520px, 100%);
  padding: 32px;
  border-radius: 24px;
  background: rgba(12, 20, 32, 0.86);
  border: 1px solid rgba(148, 163, 184, 0.24);
  box-shadow: 0 24px 60px rgba(15, 23, 42, 0.28);
  text-align: center;
}

.identity-eyebrow {
  margin: 0 0 12px;
  letter-spacing: 0.18em;
  text-transform: uppercase;
  font-size: 0.8rem;
  color: #f59e0b;
}

h1 {
  margin: 0;
}

p {
  color: rgba(226, 232, 240, 0.84);
}
</style>