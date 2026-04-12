function write(level, message, meta) {
  const timestamp = new Date().toISOString();
  if (meta === undefined) {
    console.log(`${timestamp} [${level}] ${message}`);
    return;
  }

  console.log(`${timestamp} [${level}] ${message} ${JSON.stringify(meta)}`);
}

module.exports = {
  info(message, meta) {
    write("INFO", message, meta);
  },
  warn(message, meta) {
    write("WARN", message, meta);
  },
  error(message, meta) {
    write("ERROR", message, meta);
  }
};
