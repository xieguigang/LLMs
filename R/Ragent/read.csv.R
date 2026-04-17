#' function for read csv table
#' 
#' @param file the filepath of the csv table file
#' 
#' @return this function returns a json encode text of the csv data,
#'   csv data is encoded as the row json
#' 
[@ollama "read_csv"]
const agent_readcsv = function(file) {
    let data = read.csv(file, row.names = 1, check.names = FALSE);
    data = as.list(data,byrow=TRUE);
    JSON::json_encode(data);
}