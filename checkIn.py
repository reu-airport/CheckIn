import pika, os
import json
import requests

# Access the CLODUAMQP_URL environment variable and parse it (fallback to localhost)
url = os.environ.get('CLOUDAMQP_URL', 'amqp://guest:guest@206.189.60.128')
params = pika.URLParameters(url)
connection = pika.BlockingConnection(params)
channel = connection.channel() # start a channel
channel.queue_declare(queue='CheckIn', durable = True)
def callback(ch, method, properties, body):
  body_json = json.loads(body.decode("utf-8"))
  timeStamp = body_json.get('TimeStamp')
  passengerId = body_json.get('PassengerId')
  isVip = body_json.get('Ticket').get('IsVip')
  isChecked = False
  gateNum = 3
  print(body_json)
  if body_json.get('Ticket')==None:
      body_json.update({'Reason': 'No ticket'})
      print("Passenger", passengerId, "don't have a ticket to check in")
  else:
      flightId = body_json.get('Ticket').get('FlightId')
      # здесь будет get запрос для получение рейса
      http_response = requests.get('https://info-panel222.herokuapp.com/api/v1/info-panel/'+flightId)
      #http_response = b'{"FlightId":"1","checkInBeginTime":2, "checkInEndTime":100000}'  # просто для теста
      response_json = json.loads(http_response.text)
      checkInBeginTime = response_json.get('checkInBeginTime')
      checkInEndTime = response_json.get('checkInEndTime')
      if (isVip == True):
          gateNum = 4
      if (checkInBeginTime <= timeStamp < checkInEndTime):
          isChecked = True
          body_json = body_json.get('Ticket')
          body_json.update({'GateNum': gateNum})
          body_json.update({'PassengerId': passengerId})
          time_response = requests.get('http://206.189.60.128:8083/api/v1/time')
          timeNow = json.loads(time_response.text).get('time')
          body_json.update({'TimeStamp': timeNow})
          channel.basic_publish(exchange='',
                                routing_key='GatePlacement',
                                body=json.dumps(body_json))
          print("Passenger", passengerId, "check in")
          print("Sent", json.dumps(body_json), "\n")
      if (timeStamp < checkInBeginTime):
          body_json.update({'Reason': 'Early'})
          print("Passenger", passengerId, "came early")
      if (timeStamp > checkInEndTime):
          body_json.update({'Reason': 'Late'})
          print("Passenger", passengerId, "came late")
  if (isChecked == False):
      body_json.update({'IsChecked': isChecked})
      channel.basic_publish(exchange='',
                            routing_key='CheckInToPassengerQueue',
                            body=json.dumps(body_json))
  print("Sent", json.dumps(body_json), "\n")

channel.basic_consume('CheckIn',
                      callback,
                      auto_ack=True)

print(' [*] Waiting for messages:')
channel.start_consuming()
connection.close()